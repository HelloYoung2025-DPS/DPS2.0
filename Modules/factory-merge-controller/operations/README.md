# Operations

Keep `factory_merge_controller_v1` disabled until contract and merge-head tests pass. Shadow mode compares decisions only and performs no merge or release action. The kill switch stops new decisions; rollback routes to the previous exact artifact while retaining decisions. A decision proves only merge-head eligibility, never production approval.
