using Xunit;

// The tests in this suite assert timing at the millisecond scale (the Performance
// category tolerates ±1ms) while parking workers for whole seconds to prove
// cancel/dispose semantics. Running test classes in parallel puts that load next
// to the measured delays and the millisecond assertions flake; the suite is small
// enough to be worth determinism instead.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
