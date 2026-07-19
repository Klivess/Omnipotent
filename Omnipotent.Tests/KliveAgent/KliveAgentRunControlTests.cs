using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;

namespace Omnipotent.Tests.KliveAgent
{
    public class KliveAgentRunControlTests
    {
        [Fact]
        public void Drain_ReturnsQueuedSteeringInOrder_AndEmptiesQueue()
        {
            var control = new AgentChatRunControl();
            var first = Steering("first", "message-1");
            var second = Steering("second", "message-2");
            var third = Steering("third", "message-3");

            Assert.True(control.TryEnqueue(first));
            Assert.True(control.TryEnqueue(second));
            Assert.True(control.TryEnqueue(third));

            Assert.Equal(new[] { first, second, third }, control.Drain());
            Assert.Empty(control.Drain());
        }

        [Fact]
        public void TrySeal_WhenSteeringIsQueued_DefersCompletionAndReturnsPendingInOrder()
        {
            var control = new AgentChatRunControl();
            var first = Steering("change the format", "message-1");
            var second = Steering("also include sources", "message-2");

            Assert.True(control.TryEnqueue(first));
            Assert.True(control.TryEnqueue(second));

            Assert.False(control.TrySeal(out var pending));
            Assert.Equal(new[] { first, second }, pending);

            // A failed seal is deliberately not terminal: guidance arriving while the brain
            // applies the previous batch must become another iteration, not a lost message.
            var late = Steering("one last correction", "message-3");
            Assert.True(control.TryEnqueue(late));
            Assert.False(control.TrySeal(out var latePending));
            Assert.Equal(new[] { late }, latePending);

            Assert.True(control.TrySeal(out var finalPending));
            Assert.Empty(finalPending);
        }

        [Fact]
        public void TrySeal_AfterQueueIsEmpty_AtomicallyRejectsLaterSteering()
        {
            var control = new AgentChatRunControl();

            Assert.True(control.TrySeal(out var pending));
            Assert.Empty(pending);

            Assert.False(control.TryEnqueue(Steering("too late", "message-late")));
            Assert.True(control.TrySeal(out var secondPending));
            Assert.Empty(secondPending);
        }

        [Fact]
        public void Seal_RejectsFurtherSteering_ButLeavesAlreadyAcceptedMessagesDrainable()
        {
            var control = new AgentChatRunControl();
            var accepted = Steering("accepted before stop", "message-accepted");

            Assert.True(control.TryEnqueue(accepted));
            control.Seal();

            Assert.False(control.TryEnqueue(Steering("rejected after stop", "message-rejected")));
            Assert.Equal(new[] { accepted }, control.Drain());
        }

        [Fact]
        public void Reservation_BlocksCompletion_ButIsInvisibleUntilDurablyCommitted()
        {
            var control = new AgentChatRunControl();
            var steering = Steering("save this before applying it", "message-reserved");

            Assert.True(control.TryReserve(steering));
            Assert.Empty(control.Drain());
            Assert.False(control.TrySeal(out var whilePersisting));
            Assert.Empty(whilePersisting);

            Assert.True(control.Commit(steering));
            Assert.Equal(new[] { steering }, control.Drain());
            Assert.True(control.TrySeal(out var finalPending));
            Assert.Empty(finalPending);
        }

        [Fact]
        public void RejectedOrStoppedReservation_CanNeverReachTheBrain()
        {
            var rejectedControl = new AgentChatRunControl();
            var rejected = Steering("disk write failed", "message-rejected-write");
            Assert.True(rejectedControl.TryReserve(rejected));
            rejectedControl.Reject(rejected);
            Assert.True(rejectedControl.TrySeal(out _));
            Assert.False(rejectedControl.Commit(rejected));
            Assert.Empty(rejectedControl.Drain());

            var stoppedControl = new AgentChatRunControl();
            var stopped = Steering("cancel raced with persistence", "message-stopped");
            Assert.True(stoppedControl.TryReserve(stopped));
            stoppedControl.Seal();
            Assert.False(stoppedControl.Commit(stopped));
            Assert.Empty(stoppedControl.Drain());
        }

        [Theory]
        [InlineData("conversation-123")]
        [InlineData("CONVERSATION_123")]
        [InlineData("a")]
        public void SafeIdentifier_AcceptsPortableFileNameSubset(string value)
        {
            Assert.True(global::Omnipotent.Services.KliveAgent.KliveAgent.IsSafeIdentifier(value));
        }

        [Fact]
        public void SafeIdentifier_AcceptsMaximumLength()
        {
            Assert.True(global::Omnipotent.Services.KliveAgent.KliveAgent.IsSafeIdentifier(new string('a', 128)));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("../conversation")]
        [InlineData("..\\conversation")]
        [InlineData("/root")]
        [InlineData("C:\\conversation")]
        [InlineData("conversation.json")]
        [InlineData("conversation name")]
        [InlineData("conversation/name")]
        [InlineData("conversationé")]
        public void SafeIdentifier_RejectsTraversalRootsAndNonPortableCharacters(string value)
        {
            Assert.False(global::Omnipotent.Services.KliveAgent.KliveAgent.IsSafeIdentifier(value));
        }

        [Fact]
        public void SafeIdentifier_RejectsValuesOverMaximumLength()
        {
            Assert.False(global::Omnipotent.Services.KliveAgent.KliveAgent.IsSafeIdentifier(new string('a', 129)));
        }

        private static AgentSteeringMessage Steering(string message, string messageId) => new()
        {
            Message = message,
            MessageId = messageId,
            SenderName = "test"
        };
    }
}
