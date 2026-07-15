# Migrations

The proposed length-prefixed checksum format is identified on every committed record by `checksum_encoding=dps.length-prefixed-utf8/v1`. The runtime fails closed on a missing or unknown discriminator; it never guesses whether an older newline-concatenated checksum is safe.

There is no in-place migration. If pre-discriminator test data must be retained, keep it immutable and use a separately reviewed offline export/replay tool to write a new journal path. Migration evidence must bind the source and destination file hashes, record count and order, strict identity scope, canonical payload hashes, old and new checksum heads, tool artifact digest, Release BOM, and human approval. Route to the new path only after that evidence passes. A failed comparison leaves routing on the old compatible binary and preserves both files.
