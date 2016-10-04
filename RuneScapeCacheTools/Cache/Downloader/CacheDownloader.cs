using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using log4net;
using Villermen.RuneScapeCacheTools.Cache.RuneTek5;
using Villermen.RuneScapeCacheTools.Cache.RuneTek5.Enums;
using Villermen.RuneScapeCacheTools.Extensions;

namespace Villermen.RuneScapeCacheTools.Cache.Downloader
{
    /// <summary>
    ///     The <see cref="CacheDownloader"/> provides the means to download current cache files from the runescape servers.
    /// </summary>
    /// <author>Villermen</author>
    /// <author>Method</author>
    public class CacheDownloader : CacheBase
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(CacheDownloader));

        static CacheDownloader()
        {
            // // Set the (static) security protocol used for web requests
            // ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        public int BlockLength { get; set; } = 102400;

        public bool Connected { get; private set; }

        public string ContentHost { get; set; } = "content.runescape.com";

        public int ContentPort { get; set; } = 43594;

        /// <summary>
        ///     The page used in obtaining the content server handshake key.
        /// </summary>
        public string KeyPage { get; set; } = "http://world2.runescape.com";

        /// <summary>
        ///     The regex used to obtain the content server handshake key from the set <see cref="KeyPage" />.
        ///     The first capture group needs to result in the key.
        /// </summary>
        public Regex KeyPageRegex { get; set; } = new Regex(@"<param\s+name=""1""\s+value=""([^""]+)""");

        public Language Language { get; set; } = Language.English;

        /// <summary>
        ///     The minor version is needed to correctly connect to the content server.
        ///     This seems to always be 1.
        /// </summary>
        public int MinorVersion { get; set; } = 1;

        private TcpClient ContentClient { get; set; }

        /// <summary>
        ///     The handshake type is needed to correctly connect to the content server.
        /// </summary>
        private byte HandshakeType { get; } = 15;

        private int LoadingRequirementsLength { get; } = 26 * 4;

        /// <summary>
        ///     The major version is needed to correctly connect to the content server.
        ///     If connection states the version is outdated, the <see cref="MajorVersion" /> will be increased until it is
        ///     accepted.
        /// </summary>
        private int MajorVersion { get; set; } = 873;

        private Dictionary<Tuple<Index, int>, FileRequest> PendingFileRequests { get; } =
            new Dictionary<Tuple<Index, int>, FileRequest>();

        public IList<Index> IndexesUsingHttpInterface { get; set; } = new List<Index> { Index.Music };

        public override IEnumerable<Index> Indexes => DownloadMasterReferenceTable().ReferenceTableFiles.Keys;

        public override IEnumerable<int> GetFileIds(Index index)
        {
            return DownloadReferenceTable(index).Files.Keys;
        }

        public void Connect()
        {
            var key = GetKeyFromPage();

            // Retry connecting with an increasing major version until the server no longer reports we're outdated
            var connected = false;
            while (!connected)
            {
                ContentClient = new TcpClient(ContentHost, ContentPort);

                var handshakeWriter = new BinaryWriter(ContentClient.GetStream());
                var handshakeReader = new BinaryReader(ContentClient.GetStream());

                var handshakeLength = (byte)(9 + key.Length + 1);

                handshakeWriter.Write(HandshakeType);
                handshakeWriter.Write(handshakeLength);
                handshakeWriter.WriteInt32BigEndian(MajorVersion);
                handshakeWriter.WriteInt32BigEndian(MinorVersion);
                handshakeWriter.WriteNullTerminatedString(key);
                handshakeWriter.Write((byte)Language);
                handshakeWriter.Flush();

                var response = (HandshakeResponse)handshakeReader.ReadByte();

                switch (response)
                {
                    case HandshakeResponse.Success:
                        connected = true;
                        Logger.Info($"Successfully connected to content server with major version {MajorVersion}.");
                        break;

                    case HandshakeResponse.Outdated:
                        ContentClient.Dispose();
                        ContentClient = null;
                        Logger.Info($"Content server says {MajorVersion} is outdated.");
                        MajorVersion++;
                        break;

                    default:
                        ContentClient.Dispose();
                        ContentClient = null;
                        throw new DownloaderException($"Content server responded to handshake with {response}.");
                }
            }

            // Required loading element sizes. They are unnsed by this tool and I have no idea what they are for. So yeah...
            var contentReader = new BinaryReader(ContentClient.GetStream());
            contentReader.ReadBytes(LoadingRequirementsLength);

            SendConnectionInfo();

            Connected = true;
        }

        protected override void Dispose(bool disposing)
        {
            ContentClient.Dispose();
        }

        public override CacheFile GetFile(Index index, int fileId)
        {
            return DownloadFileAsync(index, fileId).Result;
        }

        public async Task<RuneTek5CacheFile> DownloadFileAsync(Index index, int fileId)
        {
            // TODO: Properly clean and split this functionality
            if (IndexesUsingHttpInterface.Contains(index))
            {
                var referenceTableEntryHttp = index != Index.ReferenceTables ? DownloadReferenceTable(index).Files[fileId] : null;

                var webRequest = WebRequest.CreateHttp($"http://{ContentHost}/ms?m=0&a={(int)index}&g={fileId}&c={referenceTableEntryHttp.CRC}&v={referenceTableEntryHttp.Version}");
                var response = (HttpWebResponse)webRequest.GetResponse();

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new DownloaderException($"HTTP interface responsed with status code: {response.StatusCode}.");
                }

                var responseReader = new BinaryReader(response.GetResponseStream());
                var responseData = responseReader.ReadBytes((int) response.ContentLength);
                return new RuneTek5CacheFile(responseData, referenceTableEntryHttp);
            }

            if (!Connected)
            {
                throw new DownloaderException("Can't request file when disconnected.");
            }

            var writer = new BinaryWriter(ContentClient.GetStream());

            // Send the file request to the content server
            writer.Write((byte)(index == Index.ReferenceTables ? 1 : 0));
            writer.Write((byte)index);
            writer.WriteInt32BigEndian(fileId);

            var fileRequest = new FileRequest();

            var pendingFileRequestCount = PendingFileRequests.Count;

            PendingFileRequests.Add(new Tuple<Index, int>(index, fileId), fileRequest);

            // Spin up the processor when it is not running
            if (pendingFileRequestCount == 0)
            {
                Task.Run(() => ProcessRequests());
            }

            // TODO: Caching for reference tables
            var referenceTableEntry = index != Index.ReferenceTables ? DownloadReferenceTable(index).Files[fileId] : null;

            await fileRequest.WaitForCompletionAsync();

            return new RuneTek5CacheFile(fileRequest.DataStream.ToArray(), referenceTableEntry);
        }

        public MasterReferenceTable DownloadMasterReferenceTable()
        {
            return new MasterReferenceTable(GetFile(Index.ReferenceTables, (int)Index.ReferenceTables));
        }

        public ReferenceTable DownloadReferenceTable(Index index)
        {
            return new ReferenceTable(GetFile(Index.ReferenceTables, (int)index), index);
        }

        public void ProcessRequests()
        {
            while (PendingFileRequests.Count > 0)
            {
                // Read one chunk (or the leftover)
                if (ContentClient.Available >= 5)
                {
                    var reader = new BinaryReader(ContentClient.GetStream());

                    var readByteCount = 0;

                    var index = (Index)reader.ReadByte();
                    var fileId = reader.ReadInt32BigEndian() & 0x7fffffff;

                    readByteCount += 5;

                    var requestKey = new Tuple<Index, int>(index, fileId);

                    if (!PendingFileRequests.ContainsKey(requestKey))
                    {
                        throw new DownloaderException("Invalid response received (maybe not all data was consumed by the previous operation?");
                    }

                    var request = PendingFileRequests[requestKey];
                    var writer = new BinaryWriter(request.DataStream);

                    // The first part of the file always contains the filesize, which we need to know, but is also part of the file
                    if (request.FileSize == 0)
                    {
                        var compressionType = (CompressionType)reader.ReadByte();
                        var length = reader.ReadInt32BigEndian();

                        readByteCount += 5;

                        request.FileSize = 5 + (compressionType != CompressionType.None ? 4 : 0) + length;

                        writer.Write((byte)compressionType);
                        writer.WriteInt32BigEndian(length);
                    }

                    var remainingBlockLength = BlockLength - readByteCount;

                    if (remainingBlockLength > request.RemainingLength)
                    {
                        remainingBlockLength = request.RemainingLength;
                    }

                    writer.Write(reader.ReadBytes(remainingBlockLength));

                    if (request.RemainingLength == 0)
                    {
                        request.Complete();
                        PendingFileRequests.Remove(requestKey);
                    }
                }

                // var leftoverBytes = new BinaryReader(ContentClient.GetStream()).ReadBytes(ContentClient.Available);
            }
        }

        private string GetKeyFromPage()
        {
            var request = WebRequest.CreateHttp(KeyPage);
            var response = request.GetResponse();
            var responseStream = response.GetResponseStream();

            if (responseStream == null)
            {
                throw new DownloaderException($"No handshake key could be obtained from \"{KeyPage}\".");
            }

            using (var reader = new StreamReader(responseStream))
            {
                var responseString = reader.ReadToEnd();

                var key = KeyPageRegex.Match(responseString).Groups[1].Value;

                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new DownloaderException("Obtained handshake key is empty.");
                }

                return key;
            }
        }

        /// <summary>
        ///     Sends the initial connection status and login packets to the server.
        /// </summary>
        private void SendConnectionInfo()
        {
            var writer = new BinaryWriter(ContentClient.GetStream());

            // I don't know
            writer.Write((byte)6);
            writer.WriteUInt24BigEndian(4);
            writer.WriteInt16BigEndian(0);
            writer.Flush();

            writer.Write((byte)3);
            writer.WriteUInt24BigEndian(0);
            writer.WriteInt16BigEndian(0);
            writer.Flush();
        }
    }
}