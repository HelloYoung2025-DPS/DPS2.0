# Consumed contracts

`identity.binding.mutation.fence/v1` is consumed through `IBindingMutationFenceClient`. Binding atomically resolves the exact current active binding revision and returns it in a lease receipt; Persona holds the lease through its PostgreSQL commit. Persona callers never supply a binding revision or proof, and Persona never establishes or changes an identity binding.

`binding.composition.attestation/v1` is verified before production construction or database access. Persona pins Binding's production root and requires the signed Release BOM, generation, implementation/contracts/host artifact digests, instance configuration digest, and trust epoch. The runtime mutation function also binds every committed revision to the latest migrator-recorded attestation tuple. Caller-written fence implementations are rejected even when they implement the public interface.
