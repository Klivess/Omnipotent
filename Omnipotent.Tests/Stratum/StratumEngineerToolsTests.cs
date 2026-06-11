using Omnipotent.Services.Stratum;

namespace Omnipotent.Tests.Stratum
{
    public class StratumEngineerToolDefinitionTests
    {
        [Fact]
        public void ToolDefinitions_AreUniqueAndCoverThePipeline()
        {
            var tools = StratumEngineerTools.BuildToolDefinitions();
            var names = tools.Select(t => t.function.name).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

            string[] required =
            {
                "get_project_state", "update_device_plan", "update_assembly_blueprint",
                "get_dimensions", "set_dimensions", "generate_part", "measure_geometry",
                "compose_assembly", "render_views", "search_module_library", "get_module_details",
                "update_electronics_design", "update_electronics_layout", "enrich_bom",
                "write_firmware_files", "compile_firmware", "run_fea",
                "list_artifacts", "read_artifact", "request_user_approval",
            };
            foreach (var name in required)
                Assert.Contains(name, names);
        }

        [Fact]
        public void ToolDefinitions_AllHaveDescriptionsAndParameters()
        {
            foreach (var t in StratumEngineerTools.BuildToolDefinitions())
            {
                Assert.False(string.IsNullOrWhiteSpace(t.function.description), $"{t.function.name} missing description");
                Assert.NotNull(t.function.parameters);
            }
        }
    }

    public class StratumElectronicsOpsTests
    {
        [Fact]
        public void ValidateDesign_AcceptsMinimalMcuDesign()
        {
            var design = new StratumElectronicsDesign
            {
                Summary = "just an MCU",
                Modules = new List<ElectronicsModuleInstance> { new() { InstanceId = "u1", ModuleId = "mcu.esp32_devkit", Role = "brain" } },
            };
            Assert.Empty(StratumElectronicsOps.ValidateDesign(design));
        }

        [Fact]
        public void ValidateDesign_RejectsUnknownModuleAndBadPin()
        {
            var design = new StratumElectronicsDesign
            {
                Modules = new List<ElectronicsModuleInstance>
                {
                    new() { InstanceId = "u1", ModuleId = "mcu.esp32_devkit", Role = "brain" },
                    new() { InstanceId = "x1", ModuleId = "mcu.imaginary_9000", Role = "ghost" },
                },
                Wires = new List<ElectronicsWire>
                {
                    new() { FromInstance = "u1", FromPin = "NOT_A_PIN", ToInstance = "u1", ToPin = "GND", Signal = "test" },
                },
            };
            var errors = StratumElectronicsOps.ValidateDesign(design);
            Assert.Contains(errors, e => e.Contains("mcu.imaginary_9000"));
            Assert.Contains(errors, e => e.Contains("NOT_A_PIN"));
        }

        [Fact]
        public void ValidateDesign_RequiresAnMcu()
        {
            var design = new StratumElectronicsDesign
            {
                Modules = new List<ElectronicsModuleInstance> { new() { InstanceId = "m1", ModuleId = "actuator.servo_sg90", Role = "wave" } },
            };
            var errors = StratumElectronicsOps.ValidateDesign(design);
            Assert.Contains(errors, e => e.Contains("MCU"));
        }

        [Fact]
        public void ValidateAndBackfillLayout_OverwritesFootprintFromCatalog_AndChecksHosts()
        {
            var design = new StratumElectronicsDesign
            {
                Modules = new List<ElectronicsModuleInstance> { new() { InstanceId = "u1", ModuleId = "mcu.esp32_devkit", Role = "brain" } },
            };
            var layout = new StratumElectronicsLayout
            {
                Placements = new List<ElectronicsModulePlacement>
                {
                    new()
                    {
                        InstanceId = "u1", ModuleId = "mcu.esp32_devkit",
                        WorldPositionMm = new[] { 0.0, 0, 10 }, WorldRotationDeg = new[] { 0.0, 0, 0 },
                        HostingPart = "Mothership", // not a real slot
                        Footprint = new ModuleFootprint { DxMm = 1, DyMm = 1, DzMm = 1 }, // bogus — must be replaced
                    },
                },
            };
            var errors = StratumElectronicsOps.ValidateAndBackfillLayout(layout, design, new List<string> { "Base" });
            Assert.Contains(errors, e => e.Contains("Mothership"));
            // Footprint backfilled from the authoritative catalog regardless of validation outcome.
            Assert.True(layout.Placements[0].Footprint.DxMm > 10, "footprint should be the catalog's, not the bogus 1mm one");
        }
    }

    public class StratumFirmwareOpsTests
    {
        [Fact]
        public void ValidateProject_RequiresIniAndMain()
        {
            var project = new FirmwareProject
            {
                Files = new List<FirmwareFile> { new() { Path = "README.md", Content = "hi" } },
            };
            var errors = StratumFirmwareOps.ValidateProject(project);
            Assert.Contains(errors, e => e.Contains("platformio.ini"));
            Assert.Contains(errors, e => e.Contains("main.cpp"));
        }

        [Fact]
        public void ValidateProject_RejectsUnsafePaths()
        {
            var project = new FirmwareProject
            {
                Files = new List<FirmwareFile>
                {
                    new() { Path = "platformio.ini", Content = "x" },
                    new() { Path = "src/main.cpp", Content = "x" },
                    new() { Path = "../escape.txt", Content = "x" },
                },
            };
            var errors = StratumFirmwareOps.ValidateProject(project);
            Assert.Contains(errors, e => e.Contains("Unsafe"));
        }

        [Fact]
        public void EnsureIni_InjectsBoardWhenMissing()
        {
            var project = new FirmwareProject
            {
                Files = new List<FirmwareFile>
                {
                    new() { Path = "platformio.ini", Content = "; intentionally empty" },
                    new() { Path = "src/main.cpp", Content = "void setup(){} void loop(){}" },
                },
            };
            var fixedProject = StratumFirmwareOps.EnsurePlatformIOIniMatchesTarget(project, ("esp32doit-devkit-v1", "arduino"));
            var ini = fixedProject.Files.First(f => f.Path == "platformio.ini").Content;
            Assert.Contains("board = esp32doit-devkit-v1", ini);
            Assert.Contains("framework = arduino", ini);
            Assert.Contains("platform = espressif32", ini);
        }

        [Fact]
        public void ZipUnzip_RoundTrips()
        {
            var project = new FirmwareProject
            {
                Files = new List<FirmwareFile>
                {
                    new() { Path = "platformio.ini", Content = "[env:default]" },
                    new() { Path = "src/main.cpp", Content = "// firmware" },
                },
            };
            var back = StratumFirmwareOps.UnzipProject(StratumFirmwareOps.ZipProject(project));
            Assert.Equal(2, back.Files.Count);
            Assert.Equal("// firmware", back.Files.First(f => f.Path.EndsWith("main.cpp")).Content);
        }

        [Fact]
        public void ResolveTarget_FindsMcuAndBoard()
        {
            var design = new StratumElectronicsDesign
            {
                Modules = new List<ElectronicsModuleInstance> { new() { InstanceId = "u1", ModuleId = "mcu.rp2040_pico", Role = "brain" } },
            };
            var (mcu, target, error) = StratumFirmwareOps.ResolveTarget(design);
            Assert.Null(error);
            Assert.Equal("u1", mcu!.InstanceId);
            Assert.Equal("pico", target.Board);
        }
    }

    public class StratumDimensionRegistryTests
    {
        [Fact]
        public void PythonPrelude_EmitsUppercaseConstants()
        {
            string prelude = StratumDimensionRegistry.CreateDefault().ToPythonPrelude();
            Assert.Contains("WALL_THICKNESS = 2.4", prelude);
            Assert.Contains("MIN_CLEARANCE = 0.5", prelude);
        }
    }
}
