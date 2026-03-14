using System.Dynamic;
using Omnipotent.Data_Handling;

namespace Omnipotent.Tests.DataHandling
{
    public class OmniPathsTests
    {
        #region IsValidJson

        [Fact]
        public void IsValidJson_ValidObject_ReturnsTrue()
        {
            string json = """{"name":"test","value":42}""";
            Assert.True(OmniPaths.IsValidJson(json));
        }

        [Fact]
        public void IsValidJson_ValidArray_ReturnsTrue()
        {
            string json = """[1,2,3]""";
            Assert.True(OmniPaths.IsValidJson(json));
        }

        [Fact]
        public void IsValidJson_EmptyObject_ReturnsTrue()
        {
            Assert.True(OmniPaths.IsValidJson("{}"));
        }

        [Fact]
        public void IsValidJson_EmptyArray_ReturnsTrue()
        {
            Assert.True(OmniPaths.IsValidJson("[]"));
        }

        [Fact]
        public void IsValidJson_NestedObject_ReturnsTrue()
        {
            string json = """{"outer":{"inner":"value"},"list":[1,2]}""";
            Assert.True(OmniPaths.IsValidJson(json));
        }

        [Fact]
        public void IsValidJson_NullInput_ReturnsFalse()
        {
            Assert.False(OmniPaths.IsValidJson(null!));
        }

        [Fact]
        public void IsValidJson_EmptyString_ReturnsFalse()
        {
            Assert.False(OmniPaths.IsValidJson(""));
        }

        [Fact]
        public void IsValidJson_WhitespaceOnly_ReturnsFalse()
        {
            Assert.False(OmniPaths.IsValidJson("   "));
        }

        [Fact]
        public void IsValidJson_PlainText_ReturnsFalse()
        {
            Assert.False(OmniPaths.IsValidJson("not json at all"));
        }

        [Fact]
        public void IsValidJson_MalformedJson_ReturnsFalse()
        {
            Assert.False(OmniPaths.IsValidJson("{invalid json}"));
        }

        [Fact]
        public void IsValidJson_JsonWithLeadingTrailingWhitespace_ReturnsTrue()
        {
            Assert.True(OmniPaths.IsValidJson("  { \"key\": \"value\" }  "));
        }

        #endregion

        #region DoesPropertyExist

        [Fact]
        public void DoesPropertyExist_ExpandoObject_ExistingProperty_ReturnsTrue()
        {
            dynamic obj = new ExpandoObject();
            obj.Name = "Test";
            Assert.True(OmniPaths.DoesPropertyExist(obj, "Name"));
        }

        [Fact]
        public void DoesPropertyExist_ExpandoObject_NonExistingProperty_ReturnsFalse()
        {
            dynamic obj = new ExpandoObject();
            obj.Name = "Test";
            Assert.False(OmniPaths.DoesPropertyExist(obj, "Age"));
        }

        [Fact]
        public void DoesPropertyExist_AnonymousObject_ExistingProperty_ReturnsTrue()
        {
            dynamic obj = new { Name = "Test", Value = 42 };
            Assert.True(OmniPaths.DoesPropertyExist(obj, "Name"));
        }

        [Fact]
        public void DoesPropertyExist_AnonymousObject_NonExistingProperty_ReturnsFalse()
        {
            dynamic obj = new { Name = "Test" };
            Assert.False(OmniPaths.DoesPropertyExist(obj, "Missing"));
        }

        #endregion

        #region EpochMsToDateTime

        [Fact]
        public void EpochMsToDateTime_Zero_ReturnsUnixEpoch()
        {
            var result = OmniPaths.EpochMsToDateTime("0");
            Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), result);
        }

        [Fact]
        public void EpochMsToDateTime_KnownTimestamp_ReturnsCorrectDate()
        {
            // 1704067200000 ms = 2024-01-01T00:00:00Z
            var result = OmniPaths.EpochMsToDateTime("1704067200000");
            Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), result);
        }

        [Fact]
        public void EpochMsToDateTime_AnotherKnownTimestamp_ReturnsCorrectDate()
        {
            // 1000 ms = 1970-01-01T00:00:01Z
            var result = OmniPaths.EpochMsToDateTime("1000");
            Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc), result);
        }

        [Fact]
        public void EpochMsToDateTime_InvalidString_Throws()
        {
            Assert.Throws<FormatException>(() => OmniPaths.EpochMsToDateTime("notanumber"));
        }

        #endregion

        #region GetPath

        [Fact]
        public void GetPath_CombinesWithBaseDirectory()
        {
            string result = OmniPaths.GetPath("SomeFolder");
            string expected = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SomeFolder");
            Assert.Equal(expected, result);
        }

        [Fact]
        public void GetPath_NestedPath_CombinesCorrectly()
        {
            string result = OmniPaths.GetPath("Parent/Child");
            string expected = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Parent/Child");
            Assert.Equal(expected, result);
        }

        #endregion

        #region GlobalPaths structure

        [Fact]
        public void GlobalPaths_SavedDataDirectory_IsNotEmpty()
        {
            Assert.False(string.IsNullOrWhiteSpace(OmniPaths.GlobalPaths.SavedDataDirectory));
        }

        [Fact]
        public void GlobalPaths_SubDirectories_StartWithSavedData()
        {
            Assert.StartsWith(OmniPaths.GlobalPaths.SavedDataDirectory, OmniPaths.GlobalPaths.KliveBotDiscordBotDirectory);
            Assert.StartsWith(OmniPaths.GlobalPaths.SavedDataDirectory, OmniPaths.GlobalPaths.OmniscienceDirectory);
            Assert.StartsWith(OmniPaths.GlobalPaths.SavedDataDirectory, OmniPaths.GlobalPaths.CS2ArbitrageBotDirectory);
            Assert.StartsWith(OmniPaths.GlobalPaths.SavedDataDirectory, OmniPaths.GlobalPaths.OmniTraderDirectory);
        }

        #endregion
    }
}
