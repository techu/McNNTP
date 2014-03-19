﻿namespace McNNTP.Core.Client
{
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    using JetBrains.Annotations;

    [UsedImplicitly]
    public class NntpClient
    {
        public bool CanPost { get; private set; }

        [CanBeNull]
        private Connection Connection { get; set; }

        public int Port { get; set; }

        /// <summary>
        /// Gets the newsgroup currently selected by this connection
        /// </summary>
        [PublicAPI, CanBeNull]
        public string CurrentNewsgroup { get; private set; }

        /// <summary>
        /// Gets the article number currently selected by this connection for the selected newsgroup
        /// </summary>
        [PublicAPI]
        public long? CurrentArticleNumber { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="NntpClient"/> class.
        /// </summary>
        public NntpClient()
        {
            // Establish default values
            CanPost = true;
            Port = 119;
        }

        #region Connections
        public async Task Connect(string hostName, bool? tls = null)
        {
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(hostName, Port);
            Stream stream;

            if (tls ?? Port == 563)
            {
                var sslStream = new SslStream(tcpClient.GetStream());
                await sslStream.AuthenticateAsClientAsync(hostName);
                stream = sslStream;
            }
            else
                stream = tcpClient.GetStream();

            Connection = new Connection(tcpClient, stream);

            var response = await Connection.Receive();

            switch (response.Code)
            {
                case 200:
                    CanPost = true;
                    break;
                case 201:
                    CanPost = false;
                    break;
                default:
                    throw new NntpException(string.Format("Unexpected response code {0}.  Message: {1}", response.Code, response.Message));
            }
        }

        public async Task Disconnect()
        {
            await Connection.Send("QUIT\r\n");
            var response = await Connection.Receive();
            if (response.Code != 205)
                throw new NntpException(response.Message);
        }
        #endregion
        
        public async Task<ReadOnlyCollection<string>> GetCapabilities()
        {
            await Connection.Send("CAPABILITIES\r\n");
            var response = await Connection.ReceiveMultiline();
            if (response.Code != 101)
                throw new NntpException(response.Message);

            return response.Lines.ToList().AsReadOnly();
        }

        public async Task<ReadOnlyCollection<string>> GetNewsgroups()
        {
            await Connection.Send("LIST\r\n");
            var response = await Connection.ReceiveMultiline();
            if (response.Code != 215)
                throw new NntpException(response.Message);

            var retval = response.Lines.Select(line => line.Split(' ')).Select(values => values[0]).ToList();

            return retval.AsReadOnly();
        }
        public async Task<ReadOnlyCollection<string>> GetNews(string newsgroup)
        {
            var topics = new List<string>();
            await Connection.Send("GROUP {0}\r\n", newsgroup);
            var response = await Connection.Receive();
            if (response.Code != 211)
                throw new NntpException(response.Message);

            char[] seps = { ' ' };
            var values = response.Message.Split(seps);

            var start = int.Parse(values[2], CultureInfo.InvariantCulture);
            var end = int.Parse(values[3], CultureInfo.InvariantCulture);

            if (start + 100 < end && end > 100)
                start = end - 100;

            for (var i = start; i < end; i++)
            {
                await Connection.Send("ARTICLE {0}\r\n", i);
                var response2 = await Connection.ReceiveMultiline();
                if (response2.Code == 423)
                    continue;

                if (response2.Code == 220)
                    throw new NntpException(response2.Message);

                topics.AddRange(response2.Lines);
            }

            return new ReadOnlyCollection<string>(topics);
        }

        public async Task Post(string newsgroup, string subject, string from, string content)
        {
            await Connection.Send("POST\r\n");
            var response = await Connection.Receive();
            if (response.Code != 340)
                throw new NntpException(response.Message);

            await Connection.Send("From: {0}\r\nNewsgroups: {1}\r\nSubject: {2}\r\n\r\n{3}\r\n.\r\n", from, newsgroup, subject, content);
            response = await Connection.Receive();
            if (response.Code != 240)
                throw new NntpException(response.Message);
        }

        public async Task SetCurrentGroup(string newsgroup)
        {
            await Connection.Send("GROUP {0}\r\n", newsgroup);
            var response = await Connection.Receive();
            if (response.Code == 411)
                throw new NntpException("No such group: {0}", new [] { newsgroup });
        }
    }
}
