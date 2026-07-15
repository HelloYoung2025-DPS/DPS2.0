using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Json;

namespace Dps.ZennoBridge
{
    public sealed class LoopbackBridgeClient
    {
        public const string FixedEndpoint = "http://127.0.0.1:28741/dps/edge/v1/exchange";
        private const int TimeoutMilliseconds = 15000;
        internal const int MaximumResponseBytes = 64 * 1024;
        private readonly BridgePeerProofVerifier peerProofVerifier;

        public LoopbackBridgeClient()
        {
            peerProofVerifier = null;
        }

        public LoopbackBridgeClient(BridgeTrustConfiguration trustConfiguration)
        {
            peerProofVerifier = new BridgePeerProofVerifier(trustConfiguration);
        }

        public BridgeDirective Exchange(BridgeExchange exchange)
        {
            BridgeProtocolValidator.ValidateExchange(exchange);
            if (peerProofVerifier == null)
            {
                throw new BridgeProtocolException("Trusted loopback peer authentication is not configured.");
            }

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(FixedEndpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.Proxy = null;
            request.UseDefaultCredentials = true;
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.AllowAutoRedirect = false;

            DataContractJsonSerializer requestSerializer = new DataContractJsonSerializer(typeof(BridgeExchange));
            using (Stream requestStream = request.GetRequestStream())
            {
                requestSerializer.WriteObject(requestStream, exchange);
            }

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK || response.ContentType == null ||
                    !response.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new BridgeProtocolException("Loopback response is not an accepted JSON success response.");
                }

                BridgeDirective directive;
                using (Stream responseStream = response.GetResponseStream())
                {
                    directive = ReadDirective(responseStream, response.ContentLength);
                }

                BridgeProtocolValidator.ValidateDirective(directive, exchange);
                peerProofVerifier.Verify(directive, exchange.AuthNonce);
                return directive;
            }
        }

        internal static BridgeDirective ReadDirective(Stream responseStream, long declaredContentLength)
        {
            if (responseStream == null)
            {
                throw new BridgeProtocolException("Loopback response stream is missing.");
            }
            if (declaredContentLength > MaximumResponseBytes)
            {
                throw new BridgeProtocolException("Loopback response exceeds the authenticated size limit.");
            }

            byte[] buffer = new byte[4096];
            using (MemoryStream bounded = new MemoryStream())
            {
                int read;
                while ((read = responseStream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (bounded.Length + read > MaximumResponseBytes)
                    {
                        throw new BridgeProtocolException("Loopback response exceeds the authenticated size limit.");
                    }
                    bounded.Write(buffer, 0, read);
                }

                if (bounded.Length == 0)
                {
                    throw new BridgeProtocolException("Loopback response is empty.");
                }
                bounded.Position = 0;
                try
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(BridgeDirective));
                    BridgeDirective directive = serializer.ReadObject(bounded) as BridgeDirective;
                    if (directive == null)
                    {
                        throw new BridgeProtocolException("Loopback response did not contain a directive.");
                    }
                    return directive;
                }
                catch (BridgeProtocolException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new BridgeProtocolException("Loopback response JSON is invalid: " + exception.Message);
                }
            }
        }
    }
}
