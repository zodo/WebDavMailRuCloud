﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using MailRuCloudApi;
using NWebDav.Server;
using NWebDav.Server.Http;
using NWebDav.Server.Locking;
using NWebDav.Server.Logging;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;
using YaR.WebDavMailRu.CloudStore.DavCustomProperty;

namespace YaR.WebDavMailRu.CloudStore.Mailru.StoreBase
{
    [DebuggerDisplay("{_directoryInfo.FullPath}\\")]
    public sealed class MailruStoreCollection : IMailruStoreCollection
    {
        private static readonly ILogger Logger = LoggerFactory.Factory.CreateLogger(typeof(MailruStoreCollection));
        private readonly IHttpContext _context;
        private readonly Folder _directoryInfo;
        public Folder DirectoryInfo => _directoryInfo;

        public MailruStoreCollection(IHttpContext context, ILockingManager lockingManager, Folder directoryInfo, bool isWritable)
        {
            LockingManager = lockingManager;
            _context = context;
            _directoryInfo = directoryInfo;
            IsWritable = isWritable;
        }

        public static PropertyManager<MailruStoreCollection> DefaultPropertyManager { get; } = new PropertyManager<MailruStoreCollection>(new DavProperty<MailruStoreCollection>[]
        {

            new DavQuotaAvailableBytes<MailruStoreCollection>
            {
                Getter = (context, collection) => collection.FullPath == "/" ? Cloud.Instance(context).GetDiskUsage().Result.Free.DefaultValue : long.MaxValue,
                IsExpensive = true  //folder listing performance
            },

            new DavQuotaUsedBytes<MailruStoreCollection>
            {
                Getter = (context, collection) => collection.FullPath == "/" ? Cloud.Instance(context).GetDiskUsage().Result.Used.DefaultValue : long.MaxValue,
                IsExpensive = true  //folder listing performance
            },

            // RFC-2518 properties
            new DavCreationDate<MailruStoreCollection>
            {
                Getter = (context, collection) => collection._directoryInfo.CreationTimeUtc,
                Setter = (context, collection, value) =>
                {
                    collection._directoryInfo.CreationTimeUtc = value;
                    return DavStatusCode.Ok;
                }
            },
            new DavDisplayName<MailruStoreCollection>
            {
                Getter = (context, collection) => 
                collection._directoryInfo.Name
            },
            new DavGetLastModified<MailruStoreCollection>
            {
                Getter = (context, collection) => collection._directoryInfo.LastWriteTimeUtc,
                Setter = (context, collection, value) =>
                {
                    collection._directoryInfo.LastWriteTimeUtc = value;
                    return DavStatusCode.Ok;
                }
            },
            new DavGetResourceType<MailruStoreCollection>
            {
                Getter = (context, collection) => new XElement(WebDavNamespaces.DavNs + "collection")
            },

            // Default locking property handling via the LockingManager
            new DavLockDiscoveryDefault<MailruStoreCollection>(),
            new DavSupportedLockDefault<MailruStoreCollection>(),

            //Hopmann/Lippert collection properties
            new DavExtCollectionChildCount<MailruStoreCollection>
            {
                Getter = (context, collection) =>
                {
                    var data = Cloud.Instance(context).GetItems(collection.DirectoryInfo).Result;
                    int cnt = data.NumberOfItems;
                    return cnt;
                },
                IsExpensive = true  //folder listing performance
            },
            new DavExtCollectionIsFolder<MailruStoreCollection>
            {
                Getter = (context, collection) => true
            },
            new DavExtCollectionIsHidden<MailruStoreCollection>
            {
                Getter = (context, collection) => false
            },
            new DavExtCollectionIsStructuredDocument<MailruStoreCollection>
            {
                Getter = (context, collection) => false
            },

            new DavExtCollectionHasSubs<MailruStoreCollection>
            {
                Getter = (context, collection) => collection.Folders.Any(),
                IsExpensive = true  //folder listing performance
            },

            new DavExtCollectionNoSubs<MailruStoreCollection>
            {
                Getter = (context, collection) => false //TODO: WTF?
            },

            new DavExtCollectionObjectCount<MailruStoreCollection>
            {
                Getter = (context, collection) => collection.Files.Count(),
                IsExpensive = true  //folder listing performance
            },

            new DavExtCollectionReserved<MailruStoreCollection>
            {
                Getter = (context, collection) => !collection.IsWritable
            },

            //folder listing performance
            new DavExtCollectionVisibleCount<MailruStoreCollection>
            {
                Getter = (context, collection) => collection.Items.Count,
                IsExpensive = true  //folder listing performance
            },

            // Win32 extensions
            new Win32CreationTime<MailruStoreCollection>
            {
                Getter = (context, collection) => collection._directoryInfo.CreationTimeUtc,
                Setter = (context, collection, value) =>
                {
                    collection.DirectoryInfo.CreationTimeUtc = value;
                    return DavStatusCode.Ok;
                }
            },
            new Win32LastAccessTime<MailruStoreCollection>
            {
                Getter = (context, collection) => collection.DirectoryInfo.LastAccessTimeUtc,
                Setter = (context, collection, value) =>
                {
                    collection._directoryInfo.LastAccessTimeUtc = value;
                    return DavStatusCode.Ok;
                }
            },
            new Win32LastModifiedTime<MailruStoreCollection>
            {
                Getter = (context, collection) => collection.DirectoryInfo.LastWriteTimeUtc,
                Setter = (context, collection, value) =>
                {
                    collection.DirectoryInfo.LastWriteTimeUtc = value;
                    return DavStatusCode.Ok;
                }
            },
            new Win32FileAttributes<MailruStoreCollection>
            {
                Getter = (context, collection) =>  collection.DirectoryInfo.Attributes,
                Setter = (context, collection, value) =>
                {
                    collection.DirectoryInfo.Attributes = value;
                    return DavStatusCode.Ok;
                }
            },
            new DavSharedLink<MailruStoreCollection>
            {
                Getter = (context, item) => item.DirectoryInfo.PublicLink,
                Setter = (context, item, value) => DavStatusCode.Ok
            },

            new DavGetContentLength<MailruStoreCollection>
            {
                Getter = (context, item) => item.DirectoryInfo.Size.DefaultValue
            },
        });

        public bool IsWritable { get; }
        public string Name => DirectoryInfo.Name;
        public string UniqueKey => DirectoryInfo.FullPath;
        public string FullPath => DirectoryInfo.FullPath;

        public IPropertyManager PropertyManager => DefaultPropertyManager;
        public ILockingManager LockingManager { get; }


        public IList<IStoreItem> Items
        {
            get
            {
                if (null == _items)
                {
                    lock (_itemsLocker)
                    {
                        if (null == _items)
                        {
                            _items = GetItemsAsync(_context).Result;
                        }
                    }
                }
                return _items;
            }
        }

        private IList<IStoreItem> _items;
        private readonly object _itemsLocker = new object();

        public IEnumerable<MailruStoreCollection> Folders => Items.Where(it => it is MailruStoreCollection).Cast<MailruStoreCollection>();
        public IEnumerable<MailruStoreItem> Files => Items.Where(it => it is MailruStoreItem).Cast<MailruStoreItem>();


        public Task<IStoreItem> GetItemAsync(string name, IHttpContext httpContext)
        {
            var res = name == string.Empty 
                ? this
                : Items.FirstOrDefault(i => i.Name == name);

            return Task.FromResult(res);
        }

        public Task<IList<IStoreItem>> GetItemsAsync(IHttpContext httpContext)
        {
            var item = Cloud.Instance(httpContext).GetItems(_directoryInfo).Result;

            var items = item.Folders.Select(subDirectory => new MailruStoreCollection(httpContext, LockingManager, subDirectory, IsWritable)).Cast<IStoreItem>().ToList();

            items.AddRange(item.Files.Select(file => new MailruStoreItem(LockingManager, file, IsWritable)));

            return Task.FromResult<IList<IStoreItem>>(items);
        }

        public Task<StoreItemResult> CreateItemAsync(string name, bool overwrite, IHttpContext httpContext)
        {
            if (!IsWritable)
                return Task.FromResult(new StoreItemResult(DavStatusCode.PreconditionFailed));

            var destinationPath = FullPath + "/" + name;

            DavStatusCode result = DavStatusCode.Created;

            var size = httpContext.Request.ContentLength();

            //long allowedSize = Cloud.Instance.CloudApi.Account.Info.FileSizeLimit - name.BytesCount(); 
            //if (size > allowedSize)
            //{
            //    return Task.FromResult(new StoreItemResult(DavStatusCode.PreconditionFailed));
            //}

            var f = new MailRuCloudApi.File(destinationPath, size, null);

            return Task.FromResult(new StoreItemResult(result, new MailruStoreItem(LockingManager, f, IsWritable)));
        }

        public Task<StoreCollectionResult> CreateCollectionAsync(string name, bool overwrite, IHttpContext httpContext)
        {
            if (!IsWritable)
                return Task.FromResult(new StoreCollectionResult(DavStatusCode.PreconditionFailed));

            var destinationPath = WebDavPath.Combine(FullPath, name);


            var cmd = new SpecialCommand(destinationPath);
            if (cmd.IsCommand)
            {
                bool k = Cloud.Instance(httpContext).CloneItem(cmd.Path, cmd.Value).Result;
                return Task.FromResult(new StoreCollectionResult(k ? DavStatusCode.Created : DavStatusCode.PreconditionFailed));
            }


            DavStatusCode result;


            if (name != string.Empty && FindSubItem(name) != null)
            {
                if (!overwrite)
                    return Task.FromResult(new StoreCollectionResult(DavStatusCode.PreconditionFailed));

                result = DavStatusCode.NoContent;
            }
            else
                result = DavStatusCode.Created;

            try
            {
                Cloud.Instance(httpContext).CreateFolder(name, FullPath).Wait();
            }
            catch (Exception exc)
            {
                Logger.Log(LogLevel.Error, () => $"Unable to create '{destinationPath}' directory.", exc);
                return null;
            }

            return Task.FromResult(new StoreCollectionResult(result, new MailruStoreCollection(httpContext, LockingManager, new Folder(destinationPath), IsWritable)));
        }

        public Task<Stream> GetReadableStreamAsync(IHttpContext httpContext) => Task.FromResult((Stream)null);

        public Task<DavStatusCode> UploadFromStreamAsync(IHttpContext httpContext, Stream source)
        {
            throw new NotImplementedException();
        }

        public async Task<StoreItemResult> CopyAsync(IStoreCollection destinationCollection, string name, bool overwrite, IHttpContext httpContext)
        {
            // Just create the folder itself
            var result = await destinationCollection.CreateCollectionAsync(name, overwrite, httpContext);
            return new StoreItemResult(result.Result, result.Collection);
        }

        public async Task<StoreItemResult> MoveItemAsync(string sourceName, IStoreCollection destinationCollection, string destinationName, bool overwrite, IHttpContext httpContext)
        {
            // Return error
            if (!IsWritable)
                return new StoreItemResult(DavStatusCode.PreconditionFailed);

            // Determine the object that is being moved
            var item = await GetItemAsync(sourceName, httpContext);
            if (item == null)
                return new StoreItemResult(DavStatusCode.NotFound);


            var destinationStoreCollection = destinationCollection as MailruStoreCollection;

            if (destinationStoreCollection != null)
            {
                if (!destinationStoreCollection.IsWritable)
                    return new StoreItemResult(DavStatusCode.PreconditionFailed);

                var itemexist = destinationStoreCollection.FindSubItem(destinationName);

                DavStatusCode result;

                if (itemexist != null)
                {
                    if (!overwrite)
                        return new StoreItemResult(DavStatusCode.Forbidden);

                    await Cloud.Instance(httpContext).Remove(itemexist);

                    result = DavStatusCode.NoContent;
                }
                else
                    result = DavStatusCode.Created;


                if (destinationStoreCollection.FullPath == FullPath)
                    await Cloud.Instance(httpContext).Rename(item, destinationName);
                else
                    await Cloud.Instance(httpContext).Move(item, destinationStoreCollection.FullPath);

                return new StoreItemResult(result, new MailruStoreItem(LockingManager, null, IsWritable));
            }
            else
            {
                // Attempt to copy the item to the destination collection
                var result = await item.CopyAsync(destinationCollection, destinationName, overwrite, httpContext);
                if (result.Result == DavStatusCode.Created || result.Result == DavStatusCode.NoContent)
                    await DeleteItemAsync(sourceName, httpContext);

                return result;
            }
        }

        private IStoreItem FindSubItem(string name)
        {
            return string.IsNullOrEmpty(name) ? this : Items.FirstOrDefault(it => it.Name == name);
        }

        public Task<DavStatusCode> DeleteItemAsync(string name, IHttpContext httpContext)
        {
            if (!IsWritable)
                return Task.FromResult(DavStatusCode.PreconditionFailed);


            // Determine the full path
            var fullPath = Path.Combine(_directoryInfo.FullPath, name).Replace("\\", "/");
            try
            {
                var item = FindSubItem(name);

                if (null == item) return Task.FromResult(DavStatusCode.NotFound);

                Cloud.Instance(httpContext).Remove(item).Wait();
                return Task.FromResult(DavStatusCode.Ok);
            }
            catch (Exception exc)
            {
                Logger.Log(LogLevel.Error, () => $"Unable to delete '{fullPath}' directory.", exc);
                return Task.FromResult(DavStatusCode.InternalServerError);
            }
        }

        public InfiniteDepthMode InfiniteDepthMode { get; } = InfiniteDepthMode.Allowed;

        public override int GetHashCode()
        {
            return DirectoryInfo.FullPath.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            var storeCollection = obj as MailruStoreCollection;
            if (storeCollection == null)
                return false;
            return storeCollection._directoryInfo.FullPath.Equals(_directoryInfo.FullPath, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}