# Sync-Over-Async Hotpath Elimination

## Context
During a performance review, we discovered a severe anti-pattern in `ProgressTrackingStream.cs`. On every single chunk read (which can occur hundreds of times a second during video playback), the stream was calling `ObserveAsync(read).GetAwaiter().GetResult()`. Because `ObserveAsync` was an `async Task` method, the C# runtime allocated a `Task` state machine on the heap for every single read, causing massive GC (Garbage Collection) pressure and thread-pool starvation.

## Decision
We modified `ObserveAsync` to return a `ValueTask` and correctly unwrapped it using `.IsCompleted`.

## Consequences
When the observation completes synchronously (which it does 99% of the time, since it only awaits a flush every 1MB), there are **zero heap allocations**. This completely removes the GC pressure from the streaming hot-path.
