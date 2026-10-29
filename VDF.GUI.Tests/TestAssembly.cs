// Avalonia.Headless uses process-global Application/Dispatcher state. Independent UI
// test classes must not start/Dispose headless sessions concurrently: xUnit parallel
// execution occasionally threw NullReferenceException in HeadlessUnitTestSession.Dispose.
// Serial test execution is cheap (~seconds) and makes PR CI deterministic.
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
