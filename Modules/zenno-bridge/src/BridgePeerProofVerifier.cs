using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dps.ZennoBridge
{
    public sealed class BridgeTrustConfiguration
    {
        public const string SignatureAlgorithm = "rsa-pkcs1-sha256";
        private static readonly byte[] RsaAlgorithmIdentifier = new byte[]
        {
            0x30, 0x0d,
            0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01,
            0x05, 0x00
        };
        private readonly RSAParameters publicKey;

        public BridgeTrustConfiguration(
            string keyId,
            string modulusBase64,
            string exponentBase64,
            int maximumClockSkewSeconds)
        {
            byte[] modulus;
            byte[] exponent;
            try
            {
                modulus = Convert.FromBase64String(modulusBase64);
                exponent = Convert.FromBase64String(exponentBase64);
            }
            catch (FormatException exception)
            {
                throw new BridgeProtocolException("Pinned bridge public key is not valid base64: " + exception.Message);
            }

            if (modulus.Length < 256 || exponent.Length == 0)
            {
                throw new BridgeProtocolException("Pinned bridge public key must be at least RSA-2048.");
            }

            string computedKeyId = ComputeKeyId(modulus, exponent);
            if (keyId != computedKeyId)
            {
                throw new BridgeProtocolException("Pinned bridge public key does not match its key id.");
            }

            if (maximumClockSkewSeconds < 1 || maximumClockSkewSeconds > 300)
            {
                throw new BridgeProtocolException("Bridge proof clock skew must be between one and three hundred seconds.");
            }

            KeyId = keyId;
            MaximumClockSkewSeconds = maximumClockSkewSeconds;
            publicKey = new RSAParameters();
            publicKey.Modulus = (byte[])modulus.Clone();
            publicKey.Exponent = (byte[])exponent.Clone();
        }

        public string KeyId { get; private set; }

        public int MaximumClockSkewSeconds { get; private set; }

        internal RSAParameters PublicKey
        {
            get
            {
                RSAParameters copy = new RSAParameters();
                copy.Modulus = (byte[])publicKey.Modulus.Clone();
                copy.Exponent = (byte[])publicKey.Exponent.Clone();
                return copy;
            }
        }

        public static string ComputeKeyId(byte[] modulus, byte[] exponent)
        {
            if (modulus == null || exponent == null)
            {
                throw new ArgumentNullException("modulus");
            }

            if (modulus.Length == 0 || exponent.Length == 0)
            {
                throw new BridgeProtocolException("Pinned bridge public key is incomplete.");
            }

            byte[] rsaPublicKey = EncodeElement(
                0x30,
                Concatenate(EncodeInteger(modulus), EncodeInteger(exponent)));
            byte[] bitStringPayload = new byte[rsaPublicKey.Length + 1];
            Buffer.BlockCopy(rsaPublicKey, 0, bitStringPayload, 1, rsaPublicKey.Length);
            byte[] subjectPublicKeyInfo = EncodeElement(
                0x30,
                Concatenate(RsaAlgorithmIdentifier, EncodeElement(0x03, bitStringPayload)));
            return "sha256_" + Sha256Hex(subjectPublicKeyInfo);
        }

        private static byte[] EncodeInteger(byte[] unsignedBigEndian)
        {
            int offset = 0;
            while (offset < unsignedBigEndian.Length - 1 && unsignedBigEndian[offset] == 0)
            {
                offset++;
            }

            bool needsPositivePrefix = (unsignedBigEndian[offset] & 0x80) != 0;
            byte[] payload = new byte[unsignedBigEndian.Length - offset + (needsPositivePrefix ? 1 : 0)];
            Buffer.BlockCopy(
                unsignedBigEndian,
                offset,
                payload,
                needsPositivePrefix ? 1 : 0,
                unsignedBigEndian.Length - offset);
            return EncodeElement(0x02, payload);
        }

        private static byte[] EncodeElement(byte tag, byte[] payload)
        {
            byte[] length = EncodeLength(payload.Length);
            byte[] output = new byte[1 + length.Length + payload.Length];
            output[0] = tag;
            Buffer.BlockCopy(length, 0, output, 1, length.Length);
            Buffer.BlockCopy(payload, 0, output, 1 + length.Length, payload.Length);
            return output;
        }

        private static byte[] EncodeLength(int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException("length");
            }
            if (length < 128)
            {
                return new byte[] { (byte)length };
            }

            int value = length;
            int byteCount = 0;
            while (value > 0)
            {
                byteCount++;
                value >>= 8;
            }

            byte[] output = new byte[byteCount + 1];
            output[0] = (byte)(0x80 | byteCount);
            for (int index = byteCount; index > 0; index--)
            {
                output[index] = (byte)(length & 0xff);
                length >>= 8;
            }
            return output;
        }

        private static byte[] Concatenate(params byte[][] values)
        {
            int length = 0;
            int index;
            for (index = 0; index < values.Length; index++)
            {
                length += values[index].Length;
            }

            byte[] output = new byte[length];
            int offset = 0;
            for (index = 0; index < values.Length; index++)
            {
                Buffer.BlockCopy(values[index], 0, output, offset, values[index].Length);
                offset += values[index].Length;
            }
            return output;
        }

        internal static string Sha256Hex(byte[] value)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] digest = algorithm.ComputeHash(value);
                StringBuilder output = new StringBuilder(digest.Length * 2);
                int index;
                for (index = 0; index < digest.Length; index++)
                {
                    output.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
                }

                return output.ToString();
            }
        }
    }

    public sealed class BridgePeerProofVerifier
    {
        private readonly BridgeTrustConfiguration trust;
        private readonly object replayLock = new object();
        private readonly IDictionary<string, DateTimeOffset> usedNonces = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        public BridgePeerProofVerifier(BridgeTrustConfiguration trustConfiguration)
        {
            if (trustConfiguration == null)
            {
                throw new BridgeProtocolException("Trusted bridge peer configuration is required.");
            }

            trust = trustConfiguration;
        }

        public void Verify(BridgeDirective directive, string requestNonce)
        {
            DateTimeOffset issuedAt;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (directive == null)
            {
                throw new BridgeProtocolException("Directive is required for peer authentication.");
            }

            if (directive.AuthKeyId != trust.KeyId)
            {
                throw new BridgeProtocolException("Directive peer key is not pinned.");
            }

            if (!IsLowerHex(requestNonce, 64) || directive.AuthNonce != requestNonce)
            {
                throw new BridgeProtocolException("Directive authentication nonce does not match the request.");
            }

            if (String.IsNullOrEmpty(directive.AuthIssuedAt) ||
                !(directive.AuthIssuedAt.EndsWith("Z", StringComparison.Ordinal) ||
                  directive.AuthIssuedAt.EndsWith("+00:00", StringComparison.Ordinal)) ||
                !DateTimeOffset.TryParse(
                    directive.AuthIssuedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out issuedAt) ||
                issuedAt.Offset != TimeSpan.Zero)
            {
                throw new BridgeProtocolException("Directive authentication timestamp is not canonical UTC.");
            }

            if (Math.Abs((now - issuedAt).TotalSeconds) > trust.MaximumClockSkewSeconds)
            {
                throw new BridgeProtocolException("Directive authentication timestamp is outside the allowed clock window.");
            }

            string bodySha256 = ComputeDirectiveBodySha256(directive);
            if (directive.AuthBodySha256 != bodySha256)
            {
                throw new BridgeProtocolException("Directive authentication body digest mismatch.");
            }

            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(directive.AuthProof);
            }
            catch (FormatException)
            {
                throw new BridgeProtocolException("Directive authentication proof is not valid base64.");
            }
            if (!String.Equals(Convert.ToBase64String(signature), directive.AuthProof, StringComparison.Ordinal))
            {
                throw new BridgeProtocolException("Directive authentication proof is not canonical base64.");
            }

            byte[] statement = CreateSigningStatement(
                directive.AuthKeyId,
                directive.AuthNonce,
                directive.AuthIssuedAt,
                directive.AuthBodySha256);
            bool valid;
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.ImportParameters(trust.PublicKey);
                valid = rsa.VerifyData(statement, CryptoConfig.MapNameToOID("SHA256"), signature);
            }

            if (!valid)
            {
                throw new BridgeProtocolException("Directive peer authentication proof is invalid.");
            }

            lock (replayLock)
            {
                List<string> expired = new List<string>();
                foreach (KeyValuePair<string, DateTimeOffset> item in usedNonces)
                {
                    if (item.Value <= now)
                    {
                        expired.Add(item.Key);
                    }
                }

                int index;
                for (index = 0; index < expired.Count; index++)
                {
                    usedNonces.Remove(expired[index]);
                }

                if (usedNonces.ContainsKey(requestNonce))
                {
                    throw new BridgeProtocolException("Directive authentication nonce was replayed.");
                }

                usedNonces.Add(requestNonce, issuedAt.AddSeconds(trust.MaximumClockSkewSeconds));
            }
        }

        public static byte[] CreateSigningStatement(
            string keyId,
            string nonce,
            string issuedAt,
            string bodySha256)
        {
            string statement = "dps.edge.bridge.directive-auth/v1\n" + keyId + "\n" + nonce + "\n" + issuedAt + "\n" + bodySha256;
            return Encoding.UTF8.GetBytes(statement);
        }

        public static string ComputeDirectiveBodySha256(BridgeDirective directive)
        {
            if (directive == null)
            {
                throw new ArgumentNullException("directive");
            }

            StringBuilder canonical = new StringBuilder();
            AppendField(canonical, directive.SchemaVersion);
            AppendField(canonical, directive.ContractId);
            AppendField(canonical, directive.ProducerModule);
            AppendField(canonical, directive.SoulId);
            AppendField(canonical, directive.DeviceBindingId);
            AppendField(canonical, directive.PlatformAccountId);
            AppendField(canonical, directive.TraceId);
            AppendField(canonical, directive.IdempotencyKey);
            AppendField(canonical, directive.OccurredAt);
            AppendField(canonical, directive.PrivacyClass);
            AppendField(canonical, directive.DirectiveKind);
            AppendField(canonical, directive.CommandId);
            AppendField(canonical, directive.ActionKind);
            AppendField(canonical, directive.StepKind);
            AppendField(canonical, directive.Selector);
            AppendField(canonical, directive.Text);
            AppendField(canonical, directive.WaitMs.HasValue ? directive.WaitMs.Value.ToString(CultureInfo.InvariantCulture) : null);
            AppendField(canonical, directive.ExpectedPostcondition);
            return BridgeTrustConfiguration.Sha256Hex(Encoding.UTF8.GetBytes(canonical.ToString()));
        }

        private static void AppendField(StringBuilder output, string value)
        {
            if (value == null)
            {
                output.Append("-1:");
            }
            else
            {
                output.Append(value.Length.ToString(CultureInfo.InvariantCulture));
                output.Append(':');
                output.Append(value);
            }

            output.Append(';');
        }

        private static bool IsLowerHex(string value, int length)
        {
            if (value == null || value.Length != length)
            {
                return false;
            }

            int index;
            for (index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
