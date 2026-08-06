using Omnipotent.Services.KliveTechHub;
using HubService = Omnipotent.Services.KliveTechHub.KliveTechHub;

namespace Omnipotent.Tests.KliveTechHub
{
    public sealed class KliveTechFirmwareTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("..")]
        [InlineData("../escape")]
        [InlineData("folder/child")]
        [InlineData("folder\\child")]
        public void ValidateProjectName_RejectsAnythingExceptADirectChild(string value)
        {
            Assert.Throws<InvalidDataException>(() => HubService.ValidateProjectName(value));
        }

        [Fact]
        public void BuildArduinoCompileStartInfo_UsesLiteralArgumentListWithoutAShell()
        {
            string project = Path.Combine("C:\\Firmware Inbox", "project & echo unsafe");
            string output = Path.Combine("C:\\Firmware Builds", "job 1");

            System.Diagnostics.ProcessStartInfo info = HubService.BuildArduinoCompileStartInfo(
                "arduino-cli.exe",
                project,
                output,
                "esp32:esp32:esp32",
                "min_spiffs",
                libraryPath: null);

            Assert.False(info.UseShellExecute);
            Assert.Equal("arduino-cli.exe", info.FileName);
            Assert.Contains(project, info.ArgumentList);
            Assert.Contains(Path.Combine(output, "work"), info.ArgumentList);
            Assert.Contains(Path.Combine(output, "output"), info.ArgumentList);
            Assert.Contains("PartitionScheme=min_spiffs", info.ArgumentList);
        }

        [Fact]
        public void SelectFirmwareBinary_IgnoresBootloaderAndPartitionImages()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "klivetech-firmware-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string application = Path.Combine(directory, "WorkshopLight.ino.bin");
                File.WriteAllBytes(application, new byte[] { 0xE9, 0x01 });
                File.WriteAllBytes(
                    Path.Combine(directory, "WorkshopLight.ino.bootloader.bin"),
                    new byte[] { 0x01 });
                File.WriteAllBytes(
                    Path.Combine(directory, "WorkshopLight.ino.partitions.bin"),
                    new byte[] { 0x02 });

                string selected = HubService.SelectFirmwareBinary(directory, "WorkshopLight");

                Assert.Equal(application, selected);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void FirmwareOperations_KeepExistingProtocolNumbersStable()
        {
            Assert.Equal(0, (int)KliveTechActions.OperationNumber.ExecuteAction);
            Assert.Equal(1, (int)KliveTechActions.OperationNumber.GetActions);
            Assert.Equal(2, (int)KliveTechActions.OperationNumber.Ping);
            Assert.Equal(3, (int)KliveTechActions.OperationNumber.BeginFirmwareUpdate);
            Assert.Equal(6, (int)KliveTechActions.OperationNumber.AbortFirmwareUpdate);
            Assert.Equal(7, (int)KliveTechActions.OperationNumber.GetStreamables);
            Assert.Equal(8, (int)KliveTechActions.OperationNumber.ConfigureStreamable);
        }
    }
}
