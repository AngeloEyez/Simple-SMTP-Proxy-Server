﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Text.RegularExpressions;
using SmtpServer.Helpers;

namespace SmtpServer
{
    public class MailListener : TcpListener
    {
        private TcpClient client;
        private NetworkStream stream;
        private System.IO.StreamReader reader;
        private System.IO.StreamWriter writer;
        private Thread thread = null;
        private SMTPServer owner;
        const string SUBJECT = "Subject: ";
        const string FROM = "From: ";
        const string TO = "To: ";
        const string MIME_VERSION = "MIME-Version: ";
        const string DATE = "Date: ";
        const string CONTENT_TYPE = "Content-Type: ";
        const string CONTENT_TRANSFER_ENCODING = "Content-Transfer-Encoding: ";


        /// <summary>
        /// Initializes a new instance of the <see cref="MailListener"/> class.
        /// </summary>
        /// <param name="server">The server.</param>
        /// <param name="localaddr">The localaddr.</param>
        /// <param name="port">The port.</param>
        public MailListener(SMTPServer server, IPAddress localaddr, int port)
            : base(localaddr, port)
        {
            owner = server;
        }

        /// <summary>
        /// Starts listening for incoming connection requests.
        /// </summary>
        /// <PermissionSet>
        ///   <IPermission class="System.Security.Permissions.EnvironmentPermission, mscorlib, Version=2.0.3600.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" version="1" Unrestricted="true" />
        ///   <IPermission class="System.Security.Permissions.FileIOPermission, mscorlib, Version=2.0.3600.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" version="1" Unrestricted="true" />
        ///   <IPermission class="System.Security.Permissions.SecurityPermission, mscorlib, Version=2.0.3600.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" version="1" Unrestricted="true" />
        ///   <IPermission class="System.Net.SocketPermission, System, Version=2.0.3600.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" version="1" Unrestricted="true" />
        ///   </PermissionSet>
        new public void Start()
        {
            base.Start();

            client = AcceptTcpClient();
            client.ReceiveTimeout = SettingsHelper.GetIntOrDefault("ReceiveTimeout", 5000);
            stream = client.GetStream();
            reader = new System.IO.StreamReader(stream);
            writer = new System.IO.StreamWriter(stream);
            writer.NewLine = "\r\n";
            writer.AutoFlush = true;

            thread = new System.Threading.Thread(new ThreadStart(RunThread));
            thread.Start();
        }

        /// <summary>
        /// Runs the thread.
        /// </summary>
        protected void RunThread()
        {
            string line = null;

            writer.WriteLine("220 localhost -- Fake proxy server");

            try
            {
                while (reader != null)
                {
                    line = reader.ReadLine();
                    Console.Error.WriteLine("Read line {0}", line);

                    switch (line)
                    {
                        case "DATA":
                            writer.WriteLine("354 Start input, end data with <CRLF>.<CRLF>");
                            StringBuilder data = new StringBuilder();
                            String subject = "";
                            string from = "";
                            string to = "";
                            string mimeVersion = "";
                            string date = "";
                            string contentType = "";
                            string contentTransferEncoding = "";

                            line = reader.ReadLine();

                            while (line != null && line != ".")
                            {
                                if (line.StartsWith(SUBJECT))
                                {
                                    subject = line.Substring(SUBJECT.Length);
                                }
                                else if (line.StartsWith(FROM))
                                {
                                    from = line.Substring(FROM.Length);
                                }
                                else if (line.StartsWith(TO))
                                {
                                    to = line.Substring(TO.Length);
                                }
                                else if (line.StartsWith(MIME_VERSION))
                                {
                                    mimeVersion = line.Substring(MIME_VERSION.Length);
                                }
                                else if (line.StartsWith(DATE))
                                {
                                    date = line.Substring(DATE.Length);
                                }
                                else if (line.StartsWith(CONTENT_TYPE))
                                {
                                    contentType = line.Substring(CONTENT_TYPE.Length);
                                }
                                else if (line.StartsWith(CONTENT_TRANSFER_ENCODING))
                                {
                                    contentTransferEncoding = line.Substring(CONTENT_TRANSFER_ENCODING.Length);
                                }
                                else
                                {
                                    data.AppendLine(line);
                                }

                                line = reader.ReadLine();
                            }

                            String message = data.ToString();

                            WriteMessage(from, to, subject, message, contentType, contentTransferEncoding);

                            writer.WriteLine("250 OK");
                            break;

                        case "QUIT":
                            writer.WriteLine("250 OK");
                            reader = null;
                            break;

                        default:
                            writer.WriteLine("250 OK");
                            break;
                    }
                }
            }
            catch (IOException)
            {
                Console.WriteLine("Connection lost.");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            finally
            {
                client.Close();
                Stop();
            }
        }

        /// <summary>
        /// Decodes the quoted printable.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <returns></returns>
        private string DecodeQuotedPrintable(string input)
        {
            var occurences = new Regex(@"(=[0-9A-Z][0-9A-Z])+", RegexOptions.Multiline);
            var matches = occurences.Matches(input);
            foreach (Match m in matches)
            {
                byte[] bytes = new byte[m.Value.Length / 3];
                for (int i = 0; i < bytes.Length; i++)
                {
                    string hex = m.Value.Substring(i * 3 + 1, 2);
                    int iHex = Convert.ToInt32(hex, 16);
                    bytes[i] = Convert.ToByte(iHex);
                }
                input = input.Replace(m.Value, Encoding.Default.GetString(bytes));
            }
            return input.Replace("=\r\n", "");
        }

        /// <summary>
        /// Writes the message.
        /// </summary>
        /// <param name="from">From.</param>
        /// <param name="to">To.</param>
        /// <param name="subject">The subject.</param>
        /// <param name="message">The message.</param>
        /// <param name="contentType">Type of the content.</param>
        /// <param name="transferEncoding">The transfer encoding.</param>
        private void WriteMessage(string from, string to, string subject, string message, string contentType, string transferEncoding)
        {
            if (transferEncoding == "quoted-printable")
            {
                message = DecodeQuotedPrintable(message);
            }

            if (OutputToFile)
            {
                string header = string.Format("<strong>FROM: </strong>{0}<br/><strong>TO: </strong>{1}<br/><strong>SUBJECT: </strong>{2}<br/><strong>TYPE: </strong>{3}<br/><strong>ENCODING: </strong>{4}<br/><br/>",
                    new object[] { from, to, subject, contentType, transferEncoding });
                string docText = string.Format("<html><body>{0}{1}</body></html>", header, message);

                // Create a file to write to.
                string path = string.Format("mail_{0}.html", DateTime.Now.ToFileTimeUtc());
                using (StreamWriter sw = File.CreateText(path))
                {
                    sw.Write(docText);
                }
            }

            //Console.Error.WriteLine("===============================================================================");
            //Console.Error.WriteLine("Received ­email");
            //Console.Error.WriteLine("Type: " + contentType);
            //Console.Error.WriteLine("Encoding: " + transferEncoding);
            //Console.Error.WriteLine("From: " + from);
            //Console.Error.WriteLine("To: " + to);
            //Console.Error.WriteLine("Subject: " + subject);
            //Console.Error.WriteLine("-------------------------------------------------------------------------------");
            //Console.Error.WriteLine(message);
            //Console.Error.WriteLine("===============================================================================");
            //Console.Error.WriteLine("");
        }

        /// <summary>
        /// Gets or sets a value indicating whether [output to file].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [output to file]; otherwise, <c>false</c>.
        /// </value>
        public bool OutputToFile { get; set; }

        /// <summary>
        /// Gets a value indicating whether [is thread alive].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [is thread alive]; otherwise, <c>false</c>.
        /// </value>
        public bool IsThreadAlive
        {
            get { return thread.IsAlive; }
        }
    }
}