using Xunit;

// Native windows and PresentMon process snapshots share the test host's desktop.
// The explicit concurrency tests still exercise concurrent starts and stops.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
