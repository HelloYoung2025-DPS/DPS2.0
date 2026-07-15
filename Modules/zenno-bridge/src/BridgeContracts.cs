using System;
using System.Runtime.Serialization;

namespace Dps.ZennoBridge
{
    [DataContract]
    public sealed class BridgeExchange : IExtensibleDataObject
    {
        public ExtensionDataObject ExtensionData { get; set; }

        [DataMember(Name = "schema_version", IsRequired = true, Order = 1)]
        public string SchemaVersion { get; set; }

        [DataMember(Name = "contract_id", IsRequired = true, Order = 2)]
        public string ContractId { get; set; }

        [DataMember(Name = "producer_module", IsRequired = true, Order = 3)]
        public string ProducerModule { get; set; }

        [DataMember(Name = "soul_id", IsRequired = true, Order = 4)]
        public string SoulId { get; set; }

        [DataMember(Name = "device_binding_id", IsRequired = true, Order = 5)]
        public string DeviceBindingId { get; set; }

        [DataMember(Name = "platform_account_id", IsRequired = true, Order = 6)]
        public string PlatformAccountId { get; set; }

        [DataMember(Name = "trace_id", IsRequired = true, Order = 7)]
        public string TraceId { get; set; }

        [DataMember(Name = "idempotency_key", IsRequired = true, Order = 8)]
        public string IdempotencyKey { get; set; }

        [DataMember(Name = "occurred_at", IsRequired = true, Order = 9)]
        public string OccurredAt { get; set; }

        [DataMember(Name = "privacy_class", IsRequired = true, Order = 10)]
        public string PrivacyClass { get; set; }

        [DataMember(Name = "auth_nonce", IsRequired = true, Order = 11)]
        public string AuthNonce { get; set; }

        [DataMember(Name = "exchange_kind", IsRequired = true, Order = 12)]
        public string ExchangeKind { get; set; }

        [DataMember(Name = "command_id", IsRequired = true, Order = 13)]
        public string CommandId { get; set; }

        [DataMember(Name = "action_kind", IsRequired = true, Order = 14)]
        public string ActionKind { get; set; }

        [DataMember(Name = "step_kind", IsRequired = true, Order = 15)]
        public string StepKind { get; set; }

        [DataMember(Name = "selector", IsRequired = true, Order = 16)]
        public string Selector { get; set; }

        [DataMember(Name = "text", IsRequired = true, Order = 17)]
        public string Text { get; set; }

        [DataMember(Name = "wait_ms", IsRequired = true, Order = 18)]
        public int? WaitMs { get; set; }

        [DataMember(Name = "expected_postcondition", IsRequired = true, Order = 19)]
        public string ExpectedPostcondition { get; set; }

        [DataMember(Name = "native_status", IsRequired = true, Order = 20)]
        public string NativeStatus { get; set; }

        [DataMember(Name = "native_detail", IsRequired = true, Order = 21)]
        public string NativeDetail { get; set; }

        [DataMember(Name = "postcondition_verified", IsRequired = true, Order = 22)]
        public bool? PostconditionVerified { get; set; }
    }

    [DataContract]
    public sealed class BridgeDirective : IExtensibleDataObject
    {
        public ExtensionDataObject ExtensionData { get; set; }

        [DataMember(Name = "schema_version", IsRequired = true, Order = 1)]
        public string SchemaVersion { get; set; }

        [DataMember(Name = "contract_id", IsRequired = true, Order = 2)]
        public string ContractId { get; set; }

        [DataMember(Name = "producer_module", IsRequired = true, Order = 3)]
        public string ProducerModule { get; set; }

        [DataMember(Name = "soul_id", IsRequired = true, Order = 4)]
        public string SoulId { get; set; }

        [DataMember(Name = "device_binding_id", IsRequired = true, Order = 5)]
        public string DeviceBindingId { get; set; }

        [DataMember(Name = "platform_account_id", IsRequired = true, Order = 6)]
        public string PlatformAccountId { get; set; }

        [DataMember(Name = "trace_id", IsRequired = true, Order = 7)]
        public string TraceId { get; set; }

        [DataMember(Name = "idempotency_key", IsRequired = true, Order = 8)]
        public string IdempotencyKey { get; set; }

        [DataMember(Name = "occurred_at", IsRequired = true, Order = 9)]
        public string OccurredAt { get; set; }

        [DataMember(Name = "privacy_class", IsRequired = true, Order = 10)]
        public string PrivacyClass { get; set; }

        [DataMember(Name = "auth_key_id", IsRequired = true, Order = 11)]
        public string AuthKeyId { get; set; }

        [DataMember(Name = "auth_nonce", IsRequired = true, Order = 12)]
        public string AuthNonce { get; set; }

        [DataMember(Name = "auth_issued_at", IsRequired = true, Order = 13)]
        public string AuthIssuedAt { get; set; }

        [DataMember(Name = "auth_body_sha256", IsRequired = true, Order = 14)]
        public string AuthBodySha256 { get; set; }

        [DataMember(Name = "auth_proof", IsRequired = true, Order = 15)]
        public string AuthProof { get; set; }

        [DataMember(Name = "directive_kind", IsRequired = true, Order = 16)]
        public string DirectiveKind { get; set; }

        [DataMember(Name = "command_id", IsRequired = true, Order = 17)]
        public string CommandId { get; set; }

        [DataMember(Name = "action_kind", IsRequired = true, Order = 18)]
        public string ActionKind { get; set; }

        [DataMember(Name = "step_kind", IsRequired = true, Order = 19)]
        public string StepKind { get; set; }

        [DataMember(Name = "selector", IsRequired = true, Order = 20)]
        public string Selector { get; set; }

        [DataMember(Name = "text", IsRequired = true, Order = 21)]
        public string Text { get; set; }

        [DataMember(Name = "wait_ms", IsRequired = true, Order = 22)]
        public int? WaitMs { get; set; }

        [DataMember(Name = "expected_postcondition", IsRequired = true, Order = 23)]
        public string ExpectedPostcondition { get; set; }
    }
}
