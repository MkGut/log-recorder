using Xunit;

// These integration tests drive real capture through CaptureSessionRegistry,
// which enforces a single active capture per process. xUnit parallelizes test
// classes by default, so concurrent capture tests collide ("A capture is
// already active"). Run this assembly's tests serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
