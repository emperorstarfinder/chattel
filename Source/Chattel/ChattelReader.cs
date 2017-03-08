﻿// Chattel.cs
//
// Author:
//       Ricky Curtice <ricky@rwcproductions.com>
//
// Copyright (c) 2016 Richard Curtice
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using InWorldz.Data.Assets.Stratus;
using log4net;
using Nini.Config;
using OpenMetaverse;
using ProtoBuf;

namespace Chattel {
	public class ChattelReader {
		private static readonly ILog LOG = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		private List<List<IAssetServer>> _serialParallelAssetServers;

		private DirectoryInfo _cacheFolder;

		private readonly ConcurrentDictionary<string, StratusAsset> _assetsBeingWritten = new ConcurrentDictionary<string, StratusAsset>();

		/// <summary>
		/// Initializes a new instance of the <see cref="T:Chattel.Chattel"/> class.
		/// If the cachePath is null, empty, or references a folder that doesn't exist or doesn't have write access, the cache will be disabled.
		/// The serialParallelServerConfigs parameter allows you to specify server groups that shoudl be accessed serially with subgroups that should be accessed in parallel.
		/// Eg. if you have a new server you want to be hit for all operations, but to fallback to whichever of two older servers returns first, then set up a pattern like [ [ primary ], [ second1, second2 ] ].
		/// </summary>
		/// <param name="cachePath">Cache folder path.  Folder must exist or caching will be disabled.</param>
		/// <param name="serialParallelServerConfigs">Serially-accessed parallel server configs.</param>
		public ChattelReader(string cachePath = null, List<List<IAssetServerConfig>> serialParallelServerConfigs = null) {
			// Set up caching
			if (string.IsNullOrWhiteSpace(cachePath)) {
				LOG.Info($"[ASSET_READER] CachePath is empty, caching assets disabled.");
			}
			else if (!Directory.Exists(cachePath)) {
				LOG.Info($"[ASSET_READER] CachePath folder does not exist, caching assets disabled.");
			}
			else {
				_cacheFolder = new DirectoryInfo(cachePath);
				LOG.Info($"[ASSET_READER] Caching assets enabled at {_cacheFolder.FullName}");
			}

			// Set up server handlers
			if (serialParallelServerConfigs != null && serialParallelServerConfigs.Count > 0) {
				foreach (var parallelConfigs in serialParallelServerConfigs) {
					var parallelServerConnectors = new List<IAssetServer>();
					foreach (var config in parallelConfigs) {
						IAssetServer serverConnector = null;

						switch (config.Type) {
							case AssetServerType.WHIP:
								serverConnector = new AssetServerWHIP((AssetServerWHIPConfig)config);
							break;
							case AssetServerType.CF:
								serverConnector = new AssetServerCF((AssetServerCFConfig)config);
							break;
							default:
								LOG.Warn($"[ASSET_READER] Unknown asset server type {config.Type} with name {config.Name}.");
							break;
						}

						if (serverConnector != null) {
							parallelServerConnectors.Add(serverConnector);
						}
					}

					if (parallelServerConnectors.Count > 0) {
						_serialParallelAssetServers.Add(parallelServerConnectors);
					}
				}
			}
			else {
				LOG.Warn("[ASSET_READER] Servers empty or not specified. No asset servers connectors configured. Only pre-determined texture colors will be used for drawing.");
			}
		}

		public ChattelReader(IConfigSource configSource) {
			var config = configSource.Configs["Assets"];

			// Set up caching
			var cachePath = config?.GetString("CachePath", string.Empty) ?? string.Empty;

			if (string.IsNullOrWhiteSpace(cachePath)) {
				LOG.Info($"[ASSET_READER] Assets:CachePath is empty, caching assets disabled.");
			}
			else if (!Directory.Exists(cachePath)) {
				LOG.Info($"[ASSET_READER] Assets:CachePath folder does not exist, caching assets disabled.");
			}
			else {
				_cacheFolder = new DirectoryInfo(cachePath);
				LOG.Info($"[ASSET_READER] Caching assets enabled at {_cacheFolder.FullName}");
			}

			// Set up server handlers
			_serialParallelAssetServers = new List<List<IAssetServer>>();

			// Read in a config list that lists the priority order of servers and their settings.
			var sources = config?.GetString("Servers", string.Empty).Split(',').Where(source => !string.IsNullOrWhiteSpace(source)).Select(source => source.Trim());

			if (sources != null && sources.Count() > 0) {
				foreach (var source in sources) {
					var sourceConfig = configSource.Configs[source];
					IAssetServer serverConnector = null;
					var type = sourceConfig?.GetString("Type", string.Empty).ToLower();
					switch (type) {
						case "whip":
							serverConnector = new AssetServerWHIP(
								source,
								sourceConfig.GetString("Host", string.Empty),
								sourceConfig.GetInt("Port", 32700),
								sourceConfig.GetString("Password", "changeme") // Yes, that's the default password for WHIP.
							);
						break;
						case "cf":
							serverConnector = new AssetServerCF(
								source,
								sourceConfig.GetString("Username", string.Empty),
								sourceConfig.GetString("APIKey", string.Empty),
								sourceConfig.GetString("DefaultRegion", string.Empty),
								sourceConfig.GetBoolean("UseInternalURL", true),
								sourceConfig.GetString("ContainerPrefix", string.Empty)
							);
						break;
						default:
							LOG.Warn($"[ASSET_READER] Unknown asset server type in section [{source}].");
						break;
					}
					if (serverConnector != null) {
						_serialParallelAssetServers.Add(new List<IAssetServer> { serverConnector });
					}
				}
			}
			else {
				LOG.Warn("[ASSET_READER] Assets:Servers empty or not specified. No asset server sections configured. Only pre-determined texture colors will be used for drawing.");
			}
		}

		public StratusAsset GetAssetSync(UUID assetId) {
			StratusAsset result;

			// Hit up the cache first.
			if (TryGetCachedAsset(assetId, out result)) {
				return result;
			}

			// Got to go try the servers now.
			foreach (var parallelServers in _serialParallelAssetServers) {
				if (parallelServers.Count == 1) {
					result = parallelServers[0].RequestAssetSync(assetId);
				}
				else {
					result = parallelServers.AsParallel().Select(server => server.RequestAssetSync(assetId)).FirstOrDefault(asset => asset != null);
				}

				if (result != null) {
					CacheAsset(result);
					return result;
				}
			}

			return null;
		}

		private bool TryGetCachedAsset(UUID assetId, out StratusAsset asset) {
			if (_cacheFolder == null) { // Caching is disabled.
				asset = null;
				return false;
			}

			// Convert the UUID into a path.
			var path = UuidToCachePath(assetId);

			if (_assetsBeingWritten.TryGetValue(path, out asset)) {
				LOG.Debug($"[ASSET_READER] Attempted to read an asset from cache, but another thread is writing it.  Shortcutting read of {path}");
				// Asset is currently being pushed to disk, so might as well return it now since I have it in memory.
				return true;
			}

			// Attempt to read and return that file.  This needs to handle happening from multiple threads in case a given asset is read from multiple threads at the same time.
			var removeFile = false;
			try {
				using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
					asset = Serializer.Deserialize<StratusAsset>(stream);
				}
				return true;
			}
			catch (PathTooLongException e) {
				_cacheFolder = null;
				LOG.Error("[ASSET_READER] Attempted to read a cached asset, but the path was too long for the filesystem.  Disabling caching.", e);
			}
			catch (DirectoryNotFoundException) {
				// Kinda expected if that's an item that's not been cached.
			}
			catch (UnauthorizedAccessException e) {
				_cacheFolder = null;
				LOG.Error("[ASSET_READER] Attempted to read a cached asset, but this user is not allowed access.  Disabling caching.", e);
			}
			catch (FileNotFoundException) {
				// Kinda expected if that's an item that's not been cached.
			}
			catch (IOException e) {
				// This could be temporary.
				LOG.Warn("[ASSET_READER] Attempted to read a cached asset, but there was an IO error.", e);
			}
			catch (ProtoException e) {
				LOG.Warn($"[ASSET_READER] Attempted to read a cached asset, but there was a protobuf decoding error.  Removing the offending cache file as it is either corrupt or from an older installation: {path}", e);
				removeFile = true;
			}

			if (removeFile) {
				try {
					File.Delete(path);
					// TODO: at some point the folder tree should be checked for folders that should be removed.
				}
#pragma warning disable RECS0022 // A catch clause that catches System.Exception and has an empty body
				catch {
					// If there's a delete failure it'll just keep trying as the asset is called for again.
				}
#pragma warning restore RECS0022 // A catch clause that catches System.Exception and has an empty body
			}

			// Nope, no ability to get the asset.
			asset = null;
			return false;
		}

		private void CacheAsset(StratusAsset asset) {
			if (_cacheFolder == null || asset == null) { // Caching is disabled or stupidity.
				return;
			}

			var path = UuidToCachePath(asset.Id);

			if (!_assetsBeingWritten.TryAdd(path, asset)) {
				LOG.Debug($"[ASSET_READER] Attempted to write an asset to cache, but another thread is already doing so.  Skipping write of {path}");
				// Can't add it, which means it's already being written to disk by another thread.  No need to continue.
				return;
			}

			try {
				// Since UuidToCachePath always returns a path underneath the cache folder, this will only attempt to create folders there.
				Directory.CreateDirectory(Directory.GetParent(path).FullName);
				using (var file = File.Create(path)) {
					Serializer.Serialize(file, asset);
				}
				// Writing is done, remove it from the work list.
				StratusAsset temp;
				_assetsBeingWritten.TryRemove(path, out temp);
				LOG.Debug($"[ASSET_READER] Wrote an asset to cache: {path}");
			}
			catch (UnauthorizedAccessException e) {
				_cacheFolder = null;
				LOG.Error("[ASSET_READER] Attempted to write an asset to cache, but this user is not allowed access.  Disabling caching.", e);
			}
			catch (PathTooLongException e) {
				_cacheFolder = null;
				LOG.Error("[ASSET_READER] Attempted to write an asset to cache, but the path was too long for the filesystem.  Disabling caching.", e);
			}
			catch (DirectoryNotFoundException e) {
				_cacheFolder = null;
				LOG.Error("[ASSET_READER] Attempted to write an asset to cache, but cache folder was not found.  Disabling caching.", e);
			}
			catch (IOException e) {
				// This could be temporary.
				LOG.Error("[ASSET_READER] Attempted to write an asset to cache, but there was an IO error.", e);
			}
		}

		/// <summary>
		/// Converts a UUID to a path based on the cache location.
		/// </summary>
		/// <returns>The path.</returns>
		/// <param name="id">Asset identifier.</param>
		private string UuidToCachePath(UUID id) {
			return UuidToCachePath(id.Guid);
		}

		/// <summary>
		/// Converts a GUID to a path based on the cache location.
		/// </summary>
		/// <returns>The path.</returns>
		/// <param name="id">Asset identifier.</param>
		private string UuidToCachePath(Guid id) {
			var noPunctuationAssetId = id.ToString("N");
			var path = _cacheFolder.FullName;
			for (var index = 0; index < noPunctuationAssetId.Length; index += 2) {
				path = Path.Combine(path, noPunctuationAssetId.Substring(index, 2));
			}
			return path + ".pbasset";
		}
	}
}

