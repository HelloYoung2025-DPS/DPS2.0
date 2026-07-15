# Consumed contracts

The bridge produces `edge.bridge.exchange/v1` polls and native results. It consumes supervisor-owned `edge.bridge.directive/v1` commands, acknowledgements, and waits. Response identity values must exactly match the request scope.
