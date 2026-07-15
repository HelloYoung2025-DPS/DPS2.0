# Consumed contracts

- `instruction.receipt/v1` must be fresh and cover the tested module.
- `worktree.plan/v1` supplies the declarative module scope.
- `worktree.lease/v1` supplies active external fencing authority.
- `merge.request/v1` is owned by `factory-merge-controller` and produced by this runner after policy-derived checks complete.

The merge request carries evidence references only. Role assignments and required checks are retrieved by the merge controller from its own trusted policy, not trusted from this request.
