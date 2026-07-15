using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Dps.ZennoBridge
{
    public static class BridgeProtocolValidator
    {
        public const string ExchangeContract = "edge.bridge.exchange/v1";
        public const string DirectiveContract = "edge.bridge.directive/v1";
        public const string ExchangeProducer = "zenno-bridge";
        public const string DirectiveProducer = "windows-edge-supervisor";

        private static readonly IDictionary<string, string> AllowedActionSteps;
        private static readonly Regex CanonicalUtcDateTimePattern = new Regex(
            @"\A(?!0000)[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-5][0-9]:[0-5][0-9](?:\.[0-9]+)?(?:Z|\+00:00)\z",
            RegexOptions.CultureInvariant);

        static BridgeProtocolValidator()
        {
            AllowedActionSteps = new Dictionary<string, string>(StringComparer.Ordinal);
            AllowedActionSteps.Add("OBSERVE", "OBSERVE_SCREEN");
            AllowedActionSteps.Add("LOCATE", "LOCATE_SELECTOR");
            AllowedActionSteps.Add("VERIFY", "VERIFY_POSTCONDITION");
            AllowedActionSteps.Add("WAIT", "WAIT_DURATION");
            AllowedActionSteps.Add("TAP", "TAP_SELECTOR");
            AllowedActionSteps.Add("TYPE", "TYPE_TEXT");
        }

        public static void ValidateExchange(BridgeExchange exchange)
        {
            if (exchange == null)
            {
                throw new BridgeProtocolException("Exchange is required.");
            }

            if (exchange.SchemaVersion != "1.0" || exchange.ContractId != ExchangeContract || exchange.ProducerModule != ExchangeProducer)
            {
                throw new BridgeProtocolException("Unknown exchange contract identity.");
            }

            if (exchange.ExtensionData != null)
            {
                throw new BridgeProtocolException("Unknown exchange fields are forbidden.");
            }

            ValidateScope(exchange.SoulId, exchange.DeviceBindingId, exchange.PlatformAccountId, exchange.TraceId, exchange.IdempotencyKey);
            if (!IsCanonicalUtcDateTime(exchange.OccurredAt))
            {
                throw new BridgeProtocolException("Exchange occurred_at must use a canonical zero UTC offset.");
            }
            if (!IsPrivacyClass(exchange.PrivacyClass))
            {
                throw new BridgeProtocolException("Unknown exchange privacy class.");
            }
            if (!IsLowerHex(exchange.AuthNonce, 64))
            {
                throw new BridgeProtocolException("A fresh 256-bit authentication nonce is required.");
            }

            if (exchange.ExchangeKind != "POLL" && exchange.ExchangeKind != "NATIVE_RESULT")
            {
                throw new BridgeProtocolException("Unknown exchange kind.");
            }

            if (exchange.ExchangeKind == "POLL")
            {
                if (exchange.CommandId != null || exchange.ActionKind != null || exchange.StepKind != null ||
                    exchange.Selector != null || exchange.Text != null || exchange.WaitMs != null ||
                    exchange.ExpectedPostcondition != null || exchange.NativeStatus != null ||
                    exchange.NativeDetail != null || exchange.PostconditionVerified != null)
                {
                    throw new BridgeProtocolException("POLL cannot contain command or native-result fields.");
                }
            }
            else
            {
                RequireLength(exchange.CommandId, 1, 128, "command_id");
                ValidateActionStep(exchange.ActionKind, exchange.StepKind);
                if (exchange.NativeStatus != "SUCCESS" && exchange.NativeStatus != "FAILED" && exchange.NativeStatus != "UNKNOWN_OUTCOME")
                {
                    throw new BridgeProtocolException("Unknown native result status.");
                }
                RequireLength(exchange.NativeDetail, 1, 4096, "native_detail");
                RequireOptionalMaximum(exchange.Selector, 2048, "selector");
                RequireOptionalMaximum(exchange.Text, 4096, "text");
                RequireOptionalMaximum(exchange.ExpectedPostcondition, 2048, "expected_postcondition");
                if (exchange.WaitMs != null && (exchange.WaitMs < 0 || exchange.WaitMs > 300000))
                {
                    throw new BridgeProtocolException("wait_ms is outside the contract range.");
                }
                if ((exchange.ActionKind == "TAP" || exchange.ActionKind == "LOCATE" || exchange.ActionKind == "VERIFY") && String.IsNullOrWhiteSpace(exchange.Selector))
                {
                    throw new BridgeProtocolException("Selector is required for the native result.");
                }
                if (exchange.ActionKind == "TYPE" && String.IsNullOrEmpty(exchange.Text))
                {
                    throw new BridgeProtocolException("Text is required for the TYPE native result.");
                }
                if (exchange.ActionKind == "WAIT" && exchange.WaitMs == null)
                {
                    throw new BridgeProtocolException("wait_ms is required for the WAIT native result.");
                }
                if (exchange.NativeStatus == "SUCCESS" && exchange.PostconditionVerified == null)
                {
                    throw new BridgeProtocolException("SUCCESS requires an explicit postcondition result.");
                }
                if (exchange.NativeStatus == "UNKNOWN_OUTCOME" && exchange.PostconditionVerified != null)
                {
                    throw new BridgeProtocolException("UNKNOWN_OUTCOME cannot assert a postcondition result.");
                }
            }
        }

        public static void ValidateDirective(BridgeDirective directive, BridgeExchange request)
        {
            if (directive == null)
            {
                throw new BridgeProtocolException("Directive is required.");
            }

            if (directive.SchemaVersion != "1.0" || directive.ContractId != DirectiveContract || directive.ProducerModule != DirectiveProducer)
            {
                throw new BridgeProtocolException("Unknown directive contract identity.");
            }

            if (directive.ExtensionData != null)
            {
                throw new BridgeProtocolException("Unknown directive fields are forbidden.");
            }

            ValidateScope(directive.SoulId, directive.DeviceBindingId, directive.PlatformAccountId, directive.TraceId, directive.IdempotencyKey);
            if (!IsCanonicalUtcDateTime(directive.OccurredAt) || !IsCanonicalUtcDateTime(directive.AuthIssuedAt))
            {
                throw new BridgeProtocolException("Directive timestamps must use a canonical zero UTC offset.");
            }
            if (!IsPrivacyClass(directive.PrivacyClass))
            {
                throw new BridgeProtocolException("Unknown directive privacy class.");
            }
            if (!IsPrefixedLowerHex(directive.AuthKeyId, "sha256_", 64) ||
                !IsLowerHex(directive.AuthNonce, 64) ||
                !IsLowerHex(directive.AuthBodySha256, 64) ||
                !IsCanonicalBase64(directive.AuthProof, 64, 2048))
            {
                throw new BridgeProtocolException("Directive authentication fields are not canonical.");
            }
            if (directive.SoulId != request.SoulId || directive.DeviceBindingId != request.DeviceBindingId ||
                directive.PlatformAccountId != request.PlatformAccountId || directive.TraceId != request.TraceId ||
                directive.IdempotencyKey != request.IdempotencyKey || directive.PrivacyClass != request.PrivacyClass)
            {
                throw new BridgeProtocolException("Directive identity scope does not match the request.");
            }

            if (directive.DirectiveKind != "COMMAND" && directive.DirectiveKind != "ACK" && directive.DirectiveKind != "WAIT")
            {
                throw new BridgeProtocolException("Unknown directive kind.");
            }

            RequireOptionalMaximum(directive.Selector, 2048, "selector");
            RequireOptionalMaximum(directive.Text, 4096, "text");
            RequireOptionalMaximum(directive.ExpectedPostcondition, 2048, "expected_postcondition");
            if (directive.WaitMs != null && (directive.WaitMs < 0 || directive.WaitMs > 300000))
            {
                throw new BridgeProtocolException("wait_ms is outside the contract range.");
            }

            if (directive.DirectiveKind == "COMMAND")
            {
                RequireLength(directive.CommandId, 1, 128, "command_id");
                ValidateActionStep(directive.ActionKind, directive.StepKind);
                if ((directive.ActionKind == "TAP" || directive.ActionKind == "LOCATE" || directive.ActionKind == "VERIFY") && String.IsNullOrWhiteSpace(directive.Selector))
                {
                    throw new BridgeProtocolException("Selector is required for the authorized action.");
                }
                if (directive.ActionKind == "TYPE" && String.IsNullOrEmpty(directive.Text))
                {
                    throw new BridgeProtocolException("Text is required for the authorized TYPE action.");
                }
                if (directive.ActionKind == "WAIT" && directive.WaitMs == null)
                {
                    throw new BridgeProtocolException("wait_ms is required for the authorized WAIT action.");
                }
            }
            else if (directive.CommandId != null || directive.ActionKind != null ||
                     directive.StepKind != null || directive.Selector != null ||
                     directive.Text != null || directive.WaitMs != null ||
                     directive.ExpectedPostcondition != null)
            {
                throw new BridgeProtocolException("ACK and WAIT directives must not carry command fields.");
            }
        }

        private static void ValidateActionStep(string actionKind, string stepKind)
        {
            string expectedStep;
            if (String.IsNullOrWhiteSpace(actionKind) || !AllowedActionSteps.TryGetValue(actionKind, out expectedStep))
            {
                throw new BridgeProtocolException("Unknown action kind.");
            }

            if (stepKind != expectedStep)
            {
                throw new BridgeProtocolException("Unknown or mismatched step kind.");
            }
        }

        private static void ValidateScope(string soulId, string deviceBindingId, string platformAccountId, string traceId, string idempotencyKey)
        {
            if (!IsSoulId(soulId) || !IsPrefixedLowerHex(deviceBindingId, "db_", 32) || !IsPrefixedLowerHex(platformAccountId, "pa_", 32))
            {
                throw new BridgeProtocolException("Invalid canonical identity scope.");
            }

            if (!IsPrefixedLowerHex(traceId, "trace_", 32) || !IsPrefixedLowerHex(idempotencyKey, "idem_", 64))
            {
                throw new BridgeProtocolException("Trace and idempotency identifiers are required.");
            }
        }

        private static bool IsSoulId(string value)
        {
            int index;
            if (value == null || value.Length != 69 || !value.StartsWith("soul_", StringComparison.Ordinal))
            {
                return false;
            }

            for (index = 5; index < value.Length; index++)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsPrefixedLowerHex(string value, string prefix, int bodyLength)
        {
            int index;
            if (value == null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + bodyLength)
            {
                return false;
            }

            for (index = prefix.Length; index < value.Length; index++)
            {
                char character = value[index];
                bool valid = (character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f');
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsLowerHex(string value, int length)
        {
            int index;
            if (value == null || value.Length != length)
            {
                return false;
            }

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

        private static bool IsPrivacyClass(string value)
        {
            return value == "internal" || value == "personal" || value == "sensitive";
        }

        private static void RequireLength(string value, int minimum, int maximum, string field)
        {
            if (value == null || value.Length < minimum || value.Length > maximum)
            {
                throw new BridgeProtocolException(field + " length is outside the contract range.");
            }
        }

        private static void RequireOptionalMaximum(string value, int maximum, string field)
        {
            if (value != null && value.Length > maximum)
            {
                throw new BridgeProtocolException(field + " length is outside the contract range.");
            }
        }

        private static bool IsCanonicalUtcDateTime(string value)
        {
            DateTimeOffset parsed;
            if (String.IsNullOrEmpty(value) ||
                !CanonicalUtcDateTimePattern.IsMatch(value) ||
                !(value.EndsWith("Z", StringComparison.Ordinal) || value.EndsWith("+00:00", StringComparison.Ordinal)) ||
                !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
            {
                return false;
            }

            return parsed.Offset == TimeSpan.Zero;
        }

        private static bool IsCanonicalBase64(string value, int minimumLength, int maximumLength)
        {
            byte[] decoded;
            if (value == null || value.Length < minimumLength || value.Length > maximumLength)
            {
                return false;
            }

            try
            {
                decoded = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return false;
            }

            return String.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal);
        }
    }

    [Serializable]
    public sealed class BridgeProtocolException : InvalidOperationException
    {
        public BridgeProtocolException(string message) : base(message)
        {
        }
    }
}
