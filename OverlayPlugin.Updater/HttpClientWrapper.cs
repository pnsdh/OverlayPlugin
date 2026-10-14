using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Advanced_Combat_Tracker;

namespace RainbowMage.OverlayPlugin.Updater
{
    public static class HttpClientWrapper
    {
        public delegate bool ProgressInfoCallback(long resumed, long dltotal, long dlnow, long ultotal, long ulnow);
        private static readonly HttpClient client = new HttpClient();

        // Fork: a request that reads a short answer (update checks) gives up after 15 seconds
        // instead of HttpClient's 100, so an unreachable GitHub fails before the game is on screen.
        // A download waits up to 30 seconds for the server to answer, then as long as it takes.
        private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DownloadStartTimeout = TimeSpan.FromSeconds(30);
        static HttpClientWrapper()
        {
            client.DefaultRequestHeaders.Add("User-Agent", "OverlayPlugin/OverlayPlugin v" + Assembly.GetExecutingAssembly().GetName().Version.ToString());
        }

        public static void Init(string pluginDirectory)
        {
            // Nothing to do here
        }

        public static string Get(string url)
        {
            return Get(url, new Dictionary<string, string>(), null, null, false);
        }

        public static string Get(string url, Dictionary<string, string> headers, string downloadDest,
            ProgressInfoCallback infoCb, bool resume)
        {
            if (!ActGlobals.oFormActMain.InvokeRequired)
                throw new HttpClientException(false, "HttpClientWrapper called on UI thread, this can cause deadlocks");

            string result = null;
            Exception error = null;
            var retry = false;

            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri(url),
                Method = HttpMethod.Get,
            };

            foreach (var key in headers.Keys)
            {
                request.Headers.Add(key, headers[key]);
            }

            var timeout = downloadDest == null ? AnswerTimeout : DownloadStartTimeout;
            Func<Task> action = async () =>
            {
                try
                {
                    HttpResponseMessage response;
                    using (var cancel = new CancellationTokenSource(timeout))
                    {
                        // A short answer is read whole before SendAsync returns, so the timeout covers
                        // it too; a download's body is read after it, without the timeout.
                        response = await client.SendAsync(
                            request,
                            downloadDest == null ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead,
                            cancel.Token);
                    }

                    if (downloadDest == null)
                    {
                        result = await response.Content.ReadAsStringAsync();
                    }
                    else
                    {
                        var buffer = new byte[40 * 1024];
                        long length = 0;

                        long? nLength = response.Content.Headers.ContentLength;
                        if (nLength.HasValue)
                        {
                            length = nLength.Value;
                        }

                        using (var writer = File.Open(downloadDest, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                        using (var body = await response.Content.ReadAsStreamAsync())
                        {
                            var stop = false;
                            var pos = 0;
                            while (!stop)
                            {
                                var read = await body.ReadAsync(buffer, 0, buffer.Length);
                                if (read != 0)
                                {
                                    writer.Write(buffer, 0, read);
                                    pos += read;
                                }
                                else
                                    break;

                                if (infoCb != null && infoCb(0, length, pos, 0, 0))
                                    break;
                            }
                        }
                    }
                }
                catch (IOException e)
                {
                    error = e;
                    retry = true;
                }
                catch (UnauthorizedAccessException e)
                {
                    error = e;
                    retry = true;
                }
                catch (HttpRequestException e)
                {
                    error = e;
                    retry = true;
                }
                catch (OperationCanceledException e)
                {
                    error = new TimeoutException($"No answer from {request.RequestUri.Host} within {timeout.TotalSeconds} seconds.", e);
                    retry = true;
                }
                catch (Exception e)
                {
                    error = e;
                }
            };

            // Fork: waits for the task itself.  The Monitor.Pulse/Wait pair before never woke up
            // when the request failed before Wait started (e.g. no network), leaving the caller
            // waiting forever.
            Task.Run(action).Wait();

            if (error != null)
            {
                throw new HttpClientException(retry, error.Message, error);
            }
            return result;
        }
    }

    [Serializable]
    public class HttpClientException : Exception
    {
        public readonly bool Retry;

        public HttpClientException(bool retry, string message) : base(message)
        {
            this.Retry = retry;
        }

        public HttpClientException(bool retry, string message, Exception innerException) : base(message, innerException)
        {
            this.Retry = retry;
        }

        protected HttpClientException(SerializationInfo serializationInfo, StreamingContext streamingContext) : base(serializationInfo, streamingContext) { }
    }
}
