using Some.Restraint;
using System.Diagnostics;

namespace Trigger.Delay.Tests
{
    public class DelayerTest_UsingTask : TriggerDelayTest
    {
        protected override DeferBuilder BuildDelayer(uint delayMS, Action action)
        => Defer.UsingTask().ToExecute(action).ForAtLeast(delayMS).Named("TestDelayer_UsingTask");
    }

    public class DelayerTest_UsingThread : TriggerDelayTest
    {
        protected override DeferBuilder BuildDelayer(uint delayMS, Action action)
        => Defer.UsingDedicatedThread().ToExecute(action).ForAtLeast(delayMS).Named("TestDelayer_UsingThread");
    }

    public abstract class TriggerDelayTest
    {
        protected abstract DeferBuilder BuildDelayer(uint delayMS, Action action);

        private Defer CreateDeferrer(uint delayMS, Action action)
        => BuildDelayer(delayMS, action).Build();

        [Fact]
        public void Delayer_Should_Debounce_Multiple_Triggers()
        {
            // Arrange
            int executionCount = 0;
            var delayer = CreateDeferrer(100, () => executionCount++);

            // Act
            delayer.Trigger();
            delayer.Trigger();
            delayer.Trigger();
            Thread.Sleep(150); // Wait for the delay to ensure action is executed

            // Assert
            Assert.Equal(1, executionCount);
        }

        [Fact]
        public void ExecutionTime_Within10ms()
        {
            ExecutionTimeTest(100, 10, 5);
        }


        /*
         * It seems that due to inherent limitations of the windows kernel,
         * it is not possible to achieve a delay of less than 10ms with a high degree of accuracy.
         * 
         * More probably it is a limitation of my knowledge, and more work is left to be done.
         * 
         
 [TestMethod]
 public void ExecutionTime_Within5ms()
 {
     ExecutionTimeTest(100, 5, 5);
 }

 [TestMethod]
 public void ExecutionTime_Within2ms()
 {
     ExecutionTimeTest(100, 2, 5);
 }

 [TestMethod]
 public void ExecutionTime_Within1ms()
 {
     ExecutionTimeTest(100, 1, 5);

 }

*/

        public void ExecutionTimeTest(uint delayMS, int toleranceMS, int tries)
        {
            float mean = 0;
            for (int i = 0; i < tries; i++)
            {
                mean += ExecutionTimeTest(delayMS);
            }
            mean /= tries;

            Assert.InRange(delayMS+mean, delayMS - toleranceMS, delayMS + toleranceMS);
        }

        public float ExecutionTimeTest(uint delayMS = 100)
        {
            // Arrange
            var stopwatch = new Stopwatch();
            var delayer = CreateDeferrer(delayMS, () =>
            {
                stopwatch.Stop(); // Stop the measurement when the action starts executing
            });

            // Act
            stopwatch.Start();
            delayer.Trigger();

            // Wait for the action to be executed
            while (stopwatch.IsRunning)
            {
                Thread.Sleep(1);
            }

            // Assert
            long elapsedTicks = stopwatch.ElapsedTicks;

            float usecPerTick = 1000000f / Stopwatch.Frequency;
            float elapsedUsecs = elapsedTicks * usecPerTick;
            return elapsedUsecs / 1000f; // Convert to milliseconds
        }

        [Fact]
        public void Delayer_Should_Execute_Action_After_Delay()
        {
            // Arrange
            bool actionExecuted = false;
            var delayer = CreateDeferrer(100, () => actionExecuted = true);

            // Act
            delayer.Trigger();
            Thread.Sleep(150); // Wait for the delay to ensure action is executed

            // Assert
            Assert.True(actionExecuted);
        }

        [Fact]
        public void Delayer_Should_Not_Execute_Action_If_Canceled()
        {
            // Arrange
            bool actionExecuted = false;
            var delayer = CreateDeferrer(100, () => actionExecuted = true);

            // Act
            delayer.Trigger();
            delayer.CancelPending();
            Thread.Sleep(150); // Wait to ensure action is not executed

            // Assert
            Assert.False(actionExecuted);
        }

        [Fact]
        public void Delayer_Should_Not_Execute_Action_If_Paused()
        {
            // Arrange
            bool actionExecuted = false;
            var delayer = CreateDeferrer(100, () => actionExecuted = true);

            // Act
            delayer.Trigger();
            delayer.IsPaused = true;
            Thread.Sleep(150); // Wait to ensure action is not executed

            // Assert
            Assert.False(actionExecuted);

            // Act (Resume)
            delayer.IsPaused = false;
            Thread.Sleep(150); // Wait for the delay to ensure action is executed

            // Assert
            Assert.True(actionExecuted);
        }

        [Fact]
        public void Delayer_Should_Execute_Action_Immediately_With_ExecNow()
        {
            // Arrange
            bool actionExecuted = false;
            Stopwatch st = Stopwatch.StartNew();
            const int Delay = 100;
            var delayer = CreateDeferrer(Delay, () => { st.Stop(); actionExecuted = true; });

            // Act
            delayer.ExecuteNow();
            Thread.Sleep(Delay * 2); // Short wait to ensure action is executed

            // Assert
            Assert.True(st.ElapsedMilliseconds < Delay);
            Assert.True(actionExecuted);
        }

        [Fact]
        public void Delayer_Should_Execute_Action_Immediately_With_ExecNowAndWait()
        {
            // Arrange
            bool actionExecuted = false;
            const int Delay = 100;
            Stopwatch st = Stopwatch.StartNew();
            var delayer = CreateDeferrer(Delay, () => { st.Stop(); actionExecuted = true; });

            // Act
            delayer.ExecuteNowAndWait();

            // Assert
            Assert.True(actionExecuted);
            Assert.True(st.ElapsedMilliseconds < Delay);
        }

        [Fact]
        public void Delayer_Should_Respect_MaximumDelaySpan()
        {
            // Arrange
            bool actionExecuted = false;
            var delayer = BuildDelayer(100, () => actionExecuted = true)
                .WithMaximumDelay(200).Build();

            // Act
            delayer.Trigger();
            Thread.Sleep(250); // Wait past the maximum delay span

            // Assert
            Assert.True(actionExecuted);
        }

        [Fact]
        public void Delayer_Should_Respect_MaximumDelayCount()
        {
            // Arrange
            bool actionExecuted = false;
            var delayer = BuildDelayer(100, () => actionExecuted = true)
                .WithMaximumTriggerLimit(2).Build();

            // Act
            delayer.Trigger();
            delayer.Trigger();
            Thread.Sleep(150); // Wait for the delay to ensure action is executed

            // Assert
            Assert.True(actionExecuted);
        }
    }
}

