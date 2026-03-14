using Omnipotent.Service_Manager;

namespace Omnipotent.Tests.ServiceManager
{
    public class ScheduledTaskTests
    {
        #region GetTimespanRemaining

        [Fact]
        public void GetTimespanRemaining_FutureTask_ReturnsPositiveTimeSpan()
        {
            var task = new TimeManager.ScheduledTask
            {
                dateTimeDue = DateTime.Now.AddHours(1),
            };

            TimeSpan remaining = task.GetTimespanRemaining();
            Assert.True(remaining.TotalMinutes > 50);
        }

        [Fact]
        public void GetTimespanRemaining_PastTask_ReturnsZero()
        {
            var task = new TimeManager.ScheduledTask
            {
                dateTimeDue = DateTime.Now.AddHours(-1),
            };

            TimeSpan remaining = task.GetTimespanRemaining();
            Assert.Equal(TimeSpan.Zero, remaining);
        }

        [Fact]
        public void GetTimespanRemaining_TaskDueNow_ReturnsApproximatelyZero()
        {
            var task = new TimeManager.ScheduledTask
            {
                dateTimeDue = DateTime.Now,
            };

            TimeSpan remaining = task.GetTimespanRemaining();
            Assert.True(remaining.TotalSeconds < 1);
        }

        #endregion

        #region HasTaskTimePassed

        [Fact]
        public void HasTaskTimePassed_FutureTask_ReturnsFalse()
        {
            var task = new TimeManager.ScheduledTask
            {
                dateTimeDue = DateTime.Now.AddHours(1),
            };

            Assert.False(task.HasTaskTimePassed());
        }

        [Fact]
        public void HasTaskTimePassed_PastTask_ReturnsTrue()
        {
            var task = new TimeManager.ScheduledTask
            {
                dateTimeDue = DateTime.Now.AddHours(-1),
            };

            Assert.True(task.HasTaskTimePassed());
        }

        [Fact]
        public void HasTaskTimePassed_FarFuture_ReturnsFalse()
        {
            var task = new TimeManager.ScheduledTask
            {
                dateTimeDue = DateTime.Now.AddYears(1),
            };

            Assert.False(task.HasTaskTimePassed());
        }

        [Fact]
        public void HasTaskTimePassed_FarPast_ReturnsTrue()
        {
            var task = new TimeManager.ScheduledTask
            {
                dateTimeDue = DateTime.Now.AddYears(-1),
            };

            Assert.True(task.HasTaskTimePassed());
        }

        #endregion

        #region ScheduledTask Properties

        [Fact]
        public void ScheduledTask_CanSetAllProperties()
        {
            var due = DateTime.Now.AddDays(1);
            var set = DateTime.Now;

            var task = new TimeManager.ScheduledTask
            {
                taskName = "Test Task",
                dateTimeDue = due,
                dateTimeSet = set,
                agentName = "TestAgent",
                topic = "Testing",
                reason = "Unit test",
                isImportant = true,
                timeID = "12345",
                randomidentifier = "abc",
                prefired = false,
                PassableData = new { Key = "Value" },
            };

            Assert.Equal("Test Task", task.taskName);
            Assert.Equal(due, task.dateTimeDue);
            Assert.Equal(set, task.dateTimeSet);
            Assert.Equal("TestAgent", task.agentName);
            Assert.Equal("Testing", task.topic);
            Assert.Equal("Unit test", task.reason);
            Assert.True(task.isImportant);
            Assert.Equal("12345", task.timeID);
            Assert.Equal("abc", task.randomidentifier);
            Assert.False(task.prefired);
            Assert.NotNull(task.PassableData);
        }

        #endregion

        #region ThreadAnteriority Enum

        [Fact]
        public void ThreadAnteriority_HasExpectedValues()
        {
            Assert.True(Enum.IsDefined(typeof(ThreadAnteriority), ThreadAnteriority.Low));
            Assert.True(Enum.IsDefined(typeof(ThreadAnteriority), ThreadAnteriority.Standard));
            Assert.True(Enum.IsDefined(typeof(ThreadAnteriority), ThreadAnteriority.High));
            Assert.True(Enum.IsDefined(typeof(ThreadAnteriority), ThreadAnteriority.Critical));
        }

        [Fact]
        public void ThreadAnteriority_OrderIsCorrect()
        {
            Assert.True(ThreadAnteriority.Low < ThreadAnteriority.Standard);
            Assert.True(ThreadAnteriority.Standard < ThreadAnteriority.High);
            Assert.True(ThreadAnteriority.High < ThreadAnteriority.Critical);
        }

        #endregion
    }
}
