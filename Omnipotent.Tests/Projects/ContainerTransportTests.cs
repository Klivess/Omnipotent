using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects
{
    public class VncKeysymTests
    {
        [Theory]
        [InlineData("a", 0x61u)]
        [InlineData("enter", VncKeysyms.Return)]
        [InlineData("esc", VncKeysyms.Escape)]
        [InlineData("ctrl", VncKeysyms.ControlL)]
        [InlineData("f5", VncKeysyms.F1 + 4)]
        [InlineData("f12", VncKeysyms.F1 + 11)]
        [InlineData("left", VncKeysyms.Left)]
        [InlineData("space", 0x20u)]
        public void FromName_MapsKnownKeys(string name, uint expected)
        {
            Assert.Equal(expected, VncKeysyms.FromName(name));
        }

        [Fact]
        public void FromName_UnknownReturnsNull()
        {
            Assert.Null(VncKeysyms.FromName("nonsense-key"));
        }

        [Fact]
        public void FromChar_UsesX11UnicodeRuleForNonLatin()
        {
            // '€' U+20AC → 0x0100_20AC by the X11 Unicode rule.
            Assert.Equal(0x010020ACu, VncKeysyms.FromChar('€'));
        }

        [Fact]
        public void FromChar_PreservesLetterCase()
        {
            // Uppercase is produced via FromChar (+ shift in TypeText), not via FromName.
            Assert.Equal(0x41u, VncKeysyms.FromChar('A'));
            Assert.Equal(0x61u, VncKeysyms.FromChar('a'));
        }
    }

    public class InputLockCoordinatorTests
    {
        [Fact]
        public void FirstAgentAcquires_SecondIsBlocked()
        {
            var c = new InputLockCoordinator();
            Assert.True(c.TryAcquire("cont1", "agentA"));
            Assert.False(c.TryAcquire("cont1", "agentB"));
            Assert.True(c.Holds("cont1", "agentA"));
            Assert.Equal("agentA", c.CurrentHolder("cont1"));
        }

        [Fact]
        public void Release_LetsAnotherAgentAcquire()
        {
            var c = new InputLockCoordinator();
            Assert.True(c.TryAcquire("cont1", "agentA"));
            c.Release("cont1", "agentA");
            Assert.True(c.TryAcquire("cont1", "agentB"));
            Assert.Equal("agentB", c.CurrentHolder("cont1"));
        }

        [Fact]
        public void ExpiredLease_IsReclaimable()
        {
            var c = new InputLockCoordinator();
            Assert.True(c.TryAcquire("cont1", "agentA", TimeSpan.FromMilliseconds(1)));
            Thread.Sleep(20);
            Assert.False(c.Holds("cont1", "agentA")); // lease lapsed
            Assert.True(c.TryAcquire("cont1", "agentB")); // reclaimed
        }

        [Fact]
        public void SameAgentRenews()
        {
            var c = new InputLockCoordinator();
            Assert.True(c.TryAcquire("cont1", "agentA"));
            Assert.True(c.TryAcquire("cont1", "agentA")); // renew, still held
        }

        [Fact]
        public void LocksArePerContainer()
        {
            var c = new InputLockCoordinator();
            Assert.True(c.TryAcquire("cont1", "agentA"));
            Assert.True(c.TryAcquire("cont2", "agentB")); // different desktop, no contention
        }
    }

    public class ContainerRegistryTests
    {
        [Fact]
        public void ResolveForAgent_PrefersOwnContainerThenShared()
        {
            var reg = new ContainerRegistry(_ => { });
            string pid = "test_" + Guid.NewGuid().ToString("N");
            reg.Add(new DesktopContainerRecord { ContainerID = "shared1", ProjectID = pid, AgentID = null, VncHostPort = 5901 });
            reg.Add(new DesktopContainerRecord { ContainerID = "ownA", ProjectID = pid, AgentID = "agentA", VncHostPort = 5902 });

            Assert.Equal("ownA", reg.ResolveForAgent(pid, "agentA")!.ContainerID);   // own wins
            Assert.Equal("shared1", reg.ResolveForAgent(pid, "agentB")!.ContainerID); // falls back to shared
        }

        [Fact]
        public void LostContainers_AreExcludedFromResolution()
        {
            var reg = new ContainerRegistry(_ => { });
            string pid = "test_" + Guid.NewGuid().ToString("N");
            var rec = new DesktopContainerRecord { ContainerID = "ownA", ProjectID = pid, AgentID = "agentA", VncHostPort = 5902, Lost = true };
            reg.Add(rec);
            Assert.Null(reg.ResolveForAgent(pid, "agentA"));
        }
    }
}
