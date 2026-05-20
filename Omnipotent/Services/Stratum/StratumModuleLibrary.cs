namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Curated electronics-module catalog. The Electronics Agent constrains module selection to
    /// these entries so its wiring output is grounded in real, commercially-available parts with
    /// known pinouts. Each entry carries a <see cref="MouserKeyword"/> used by
    /// <see cref="StratumPartsCatalog"/> to enrich the BOM with live distributor data
    /// (price, datasheet, stock) at design time. Distributor APIs (Mouser/Digi-Key/Octopart) do
    /// NOT expose structured pinouts — those live in PDF datasheets — so curated specs remain the
    /// agent's source of truth for wiring.
    /// </summary>
    public static class StratumModuleLibrary
    {
        public static readonly IReadOnlyList<ModuleSpec> Modules = BuildLibrary();

        public static ModuleSpec? Find(string id) =>
            Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Compact LLM-friendly catalog string. Pin kinds let the agent reason about compatibility.</summary>
        public static string ToPromptCatalog()
        {
            var lines = new List<string>();
            foreach (var m in Modules)
            {
                string pins = string.Join(", ", m.Pins.Select(p => $"{p.Name}({p.Kind})"));
                lines.Add($"- {m.Id} | {m.Category} | {m.Description} | V={m.OperatingVoltage} | I={m.MaxCurrent} | pins: {pins}");
            }
            return string.Join("\n", lines);
        }

        private static IReadOnlyList<ModuleSpec> BuildLibrary()
        {
            var list = new List<ModuleSpec>();

            // ── MCUs ──
            list.Add(new ModuleSpec("mcu.arduino_nano", "MCU",
                "Arduino Nano (ATmega328P) — 5V logic, 14 GPIO, 6 PWM, 8 ADC, USB",
                "5 V (Vin 7-12 V)", "200 mA reg",
                mouserKeyword: "Arduino Nano ATmega328",
                pins: new[] {
                    P("5V","power_out"), P("3V3","power_out"), P("GND","gnd"), P("VIN","power_in"),
                    P("D0/RX","gpio"), P("D1/TX","gpio"), P("D2","gpio"), P("D3~","pwm"), P("D4","gpio"),
                    P("D5~","pwm"), P("D6~","pwm"), P("D7","gpio"), P("D8","gpio"), P("D9~","pwm"),
                    P("D10~","pwm"), P("D11~","pwm"), P("D12","gpio"), P("D13","gpio"),
                    P("A0","analog"), P("A1","analog"), P("A2","analog"), P("A3","analog"),
                    P("A4/SDA","i2c_sda"), P("A5/SCL","i2c_scl"),
                }).With(new ModuleFootprint
                {
                    DxMm = 18.5, DyMm = 43.2, DzMm = 11.0,
                    MountStrategy = "PCB-headers",
                    Connectors = new List<ConnectorAccess>
                    {
                        new() { Kind = "usb-micro", LocalPositionMm = new[] { 0.0, 21.6, 6.0 }, Direction = "+Y", CutoutSizeMm = new[] { 8.0, 3.5 } }
                    }
                }));
            list.Add(new ModuleSpec("mcu.esp32_devkit", "MCU",
                "ESP32 DevKit V1 — Wi-Fi/BT, 3.3 V logic, ~30 GPIO",
                "3.3 V (USB or 5 V VIN)", "500 mA reg",
                mouserKeyword: "ESP32 DevKitC",
                pins: new[] {
                    P("3V3","power_out"), P("5V","power_in"), P("GND","gnd"),
                    P("GPIO2","gpio"), P("GPIO4","gpio"), P("GPIO5","gpio"),
                    P("GPIO12","gpio"), P("GPIO13","gpio"), P("GPIO14","pwm"),
                    P("GPIO15","gpio"), P("GPIO16","gpio"), P("GPIO17","gpio"),
                    P("GPIO18","spi_sck"), P("GPIO19","spi_miso"), P("GPIO21","i2c_sda"),
                    P("GPIO22","i2c_scl"), P("GPIO23","spi_mosi"),
                    P("GPIO25","dac"), P("GPIO26","dac"), P("GPIO27","gpio"),
                    P("GPIO32","analog"), P("GPIO33","analog"), P("GPIO34","analog_in"),
                    P("GPIO35","analog_in"), P("GPIO36","analog_in"), P("GPIO39","analog_in"),
                }).With(new ModuleFootprint
                {
                    DxMm = 27.9, DyMm = 55.8, DzMm = 13.5,
                    MountStrategy = "PCB-headers",
                    Connectors = new List<ConnectorAccess>
                    {
                        new() { Kind = "usb-micro", LocalPositionMm = new[] { 0.0, 27.9, 7.0 }, Direction = "+Y", CutoutSizeMm = new[] { 9.0, 4.0 } }
                    }
                }));
            list.Add(new ModuleSpec("mcu.rp2040_pico", "MCU",
                "Raspberry Pi Pico (RP2040) — 3.3 V logic, 26 GPIO, hardware PIO",
                "3.3 V (1.8-5.5 V VSYS)", "300 mA reg",
                mouserKeyword: "Raspberry Pi Pico RP2040",
                pins: new[] {
                    P("VSYS","power_in"), P("3V3","power_out"), P("GND","gnd"),
                    P("GP0","gpio"), P("GP1","gpio"), P("GP2","gpio"), P("GP3","gpio"),
                    P("GP4","i2c_sda"), P("GP5","i2c_scl"),
                    P("GP6","gpio"), P("GP7","gpio"), P("GP8","gpio"), P("GP9","gpio"),
                    P("GP10","gpio"), P("GP11","gpio"), P("GP12","gpio"), P("GP13","gpio"),
                    P("GP14","gpio"), P("GP15","gpio"),
                    P("GP16","spi_miso"), P("GP17","spi_cs"), P("GP18","spi_sck"), P("GP19","spi_mosi"),
                    P("GP20","gpio"), P("GP21","gpio"), P("GP22","gpio"),
                    P("GP26","analog"), P("GP27","analog"), P("GP28","analog"),
                }).With(new ModuleFootprint
                {
                    DxMm = 21.0, DyMm = 51.0, DzMm = 4.0,
                    MountStrategy = "screw-bosses",
                    MountHolesMm = new List<double[]>
                    {
                        new[] { -8.0,  22.5, 2.2 },
                        new[] {  8.0,  22.5, 2.2 },
                        new[] { -8.0, -22.5, 2.2 },
                        new[] {  8.0, -22.5, 2.2 },
                    },
                    Connectors = new List<ConnectorAccess>
                    {
                        new() { Kind = "usb-micro", LocalPositionMm = new[] { 0.0, 25.5, 2.0 }, Direction = "+Y", CutoutSizeMm = new[] { 8.0, 3.5 } }
                    }
                }));

            // ── Motor drivers ──
            list.Add(new ModuleSpec("driver.l298n", "MotorDriver",
                "L298N dual H-bridge — drives 2× brushed DC motors @ up to 2 A each",
                "5-35 V motor, 5 V logic", "2 A/ch",
                mouserKeyword: "STMicroelectronics L298N",
                pins: new[] {
                    P("VCC","power_in"), P("GND","gnd"), P("5V","power_out"),
                    P("ENA","pwm_in"), P("IN1","gpio_in"), P("IN2","gpio_in"),
                    P("IN3","gpio_in"), P("IN4","gpio_in"), P("ENB","pwm_in"),
                    P("OUT1","motor_out"), P("OUT2","motor_out"),
                    P("OUT3","motor_out"), P("OUT4","motor_out"),
                }).With(new ModuleFootprint
                {
                    DxMm = 43.0, DyMm = 43.0, DzMm = 27.0,
                    MountStrategy = "screw-bosses",
                    MountHolesMm = new List<double[]>
                    {
                        new[] { -18.5, -18.5, 3.2 },
                        new[] {  18.5, -18.5, 3.2 },
                        new[] { -18.5,  18.5, 3.2 },
                        new[] {  18.5,  18.5, 3.2 },
                    },
                    Connectors = new List<ConnectorAccess>
                    {
                        new() { Kind = "screw-terminal", LocalPositionMm = new[] { 0.0, -21.5, 10.0 }, Direction = "-Y", CutoutSizeMm = new[] { 30.0, 12.0 } },
                        new() { Kind = "screw-terminal", LocalPositionMm = new[] { 0.0,  21.5, 10.0 }, Direction = "+Y", CutoutSizeMm = new[] { 30.0, 12.0 } },
                    }
                }));
            list.Add(new ModuleSpec("driver.tb6612fng", "MotorDriver",
                "TB6612FNG dual H-bridge — drives 2× brushed DC motors @ 1.2 A each (3 A peak)",
                "2.5-13.5 V motor, 2.7-5.5 V logic", "1.2 A/ch",
                mouserKeyword: "Toshiba TB6612FNG",
                pins: new[] {
                    P("VM","power_in"), P("VCC","power_in"), P("GND","gnd"),
                    P("STBY","gpio_in"),
                    P("AIN1","gpio_in"), P("AIN2","gpio_in"), P("PWMA","pwm_in"),
                    P("BIN1","gpio_in"), P("BIN2","gpio_in"), P("PWMB","pwm_in"),
                    P("AO1","motor_out"), P("AO2","motor_out"),
                    P("BO1","motor_out"), P("BO2","motor_out"),
                }).With(new ModuleFootprint
                {
                    DxMm = 20.0, DyMm = 20.0, DzMm = 5.0,
                    MountStrategy = "PCB-headers",
                }));
            list.Add(new ModuleSpec("driver.a4988", "StepperDriver",
                "A4988 microstepping stepper driver — 1 stepper, up to 2 A/coil, 1/16 microstep",
                "8-35 V motor, 3-5.5 V logic", "2 A/coil",
                mouserKeyword: "Allegro A4988",
                pins: new[] {
                    P("VMOT","power_in"), P("GND","gnd"), P("VDD","power_in"),
                    P("STEP","gpio_in"), P("DIR","gpio_in"), P("EN","gpio_in"),
                    P("MS1","gpio_in"), P("MS2","gpio_in"), P("MS3","gpio_in"),
                    P("1A","motor_out"), P("1B","motor_out"), P("2A","motor_out"), P("2B","motor_out"),
                }).With(new ModuleFootprint
                {
                    DxMm = 15.0, DyMm = 20.0, DzMm = 12.0,
                    MountStrategy = "PCB-headers",
                }));

            // ── Sensors ──
            list.Add(new ModuleSpec("sensor.mpu6050", "Sensor",
                "MPU-6050 6-axis IMU (accel + gyro) over I²C",
                "3.3-5 V", "5 mA",
                mouserKeyword: "InvenSense MPU-6050",
                pins: new[] { P("VCC","power_in"), P("GND","gnd"), P("SDA","i2c_sda"), P("SCL","i2c_scl"), P("INT","gpio_out") })
                .With(new ModuleFootprint
                {
                    DxMm = 16.0, DyMm = 20.0, DzMm = 4.0,
                    MountStrategy = "screw-bosses",
                    MountHolesMm = new List<double[]>
                    {
                        new[] { -5.5, -7.5, 2.2 },
                        new[] {  5.5, -7.5, 2.2 },
                    }
                }));
            list.Add(new ModuleSpec("sensor.hcsr04", "Sensor",
                "HC-SR04 ultrasonic distance sensor (2-400 cm)",
                "5 V", "15 mA",
                mouserKeyword: "HC-SR04 ultrasonic",
                pins: new[] { P("VCC","power_in"), P("GND","gnd"), P("TRIG","gpio_in"), P("ECHO","gpio_out") })
                .With(new ModuleFootprint
                {
                    DxMm = 45.0, DyMm = 20.0, DzMm = 15.0,
                    MountStrategy = "screw-bosses",
                    MountHolesMm = new List<double[]>
                    {
                        new[] { -20.5,  6.0, 2.2 },
                        new[] {  20.5,  6.0, 2.2 },
                        new[] { -20.5, -6.0, 2.2 },
                        new[] {  20.5, -6.0, 2.2 },
                    },
                    Connectors = new List<ConnectorAccess>
                    {
                        new() { Kind = "led-indicator", LocalPositionMm = new[] { -10.0, 0.0, 7.5 }, Direction = "+Z", CutoutSizeMm = new[] { 16.0, 16.0 } },
                        new() { Kind = "led-indicator", LocalPositionMm = new[] {  10.0, 0.0, 7.5 }, Direction = "+Z", CutoutSizeMm = new[] { 16.0, 16.0 } },
                    }
                }));
            list.Add(new ModuleSpec("sensor.dht22", "Sensor",
                "DHT22 / AM2302 temperature + humidity sensor (1-Wire)",
                "3.3-5 V", "1.5 mA",
                mouserKeyword: "DHT22 AM2302",
                pins: new[] { P("VCC","power_in"), P("GND","gnd"), P("DATA","gpio_inout") })
                .With(new ModuleFootprint
                {
                    DxMm = 15.0, DyMm = 25.0, DzMm = 8.0,
                    MountStrategy = "wire-mount",
                    Connectors = new List<ConnectorAccess>
                    {
                        new() { Kind = "led-indicator", LocalPositionMm = new[] { 0.0, 0.0, 4.0 }, Direction = "+Z", CutoutSizeMm = new[] { 12.0, 22.0 } },
                    }
                }));
            list.Add(new ModuleSpec("sensor.bmp280", "Sensor",
                "BMP280 pressure + temperature sensor (I²C/SPI)",
                "3.3 V", "1 mA",
                mouserKeyword: "Bosch BMP280",
                pins: new[] { P("VCC","power_in"), P("GND","gnd"), P("SDA","i2c_sda"), P("SCL","i2c_scl") })
                .With(new ModuleFootprint
                {
                    DxMm = 11.0, DyMm = 13.0, DzMm = 3.0,
                    MountStrategy = "screw-bosses",
                    MountHolesMm = new List<double[]>
                    {
                        new[] { -3.5, -4.5, 2.0 },
                        new[] {  3.5, -4.5, 2.0 },
                    }
                }));
            list.Add(new ModuleSpec("sensor.ina219", "Sensor",
                "INA219 current/voltage/power sensor (I²C, high-side)",
                "3-5.5 V", "1 mA",
                mouserKeyword: "Texas Instruments INA219",
                pins: new[] { P("VCC","power_in"), P("GND","gnd"), P("SDA","i2c_sda"), P("SCL","i2c_scl"),
                    P("VIN+","sense_in"), P("VIN-","sense_in") })
                .With(new ModuleFootprint
                {
                    DxMm = 16.0, DyMm = 25.0, DzMm = 4.0,
                    MountStrategy = "screw-bosses",
                    MountHolesMm = new List<double[]>
                    {
                        new[] { -5.5,  10.0, 2.2 },
                        new[] {  5.5,  10.0, 2.2 },
                    }
                }));

            // ── Actuators ──
            list.Add(new ModuleSpec("actuator.dc_motor_12v", "Actuator",
                "Generic 12 V brushed DC gearmotor (e.g. 100 RPM, 1.2 Nm)",
                "12 V", "1 A nominal, 3 A stall",
                mouserKeyword: "12V DC gearmotor",
                pins: new[] { P("M+","motor_in"), P("M-","motor_in") })
                .With(new ModuleFootprint
                {
                    DxMm = 37.0, DyMm = 65.0, DzMm = 22.0,
                    MountStrategy = "screw-bosses",
                    MountHolesMm = new List<double[]>
                    {
                        new[] { -15.0, -28.0, 3.2 },
                        new[] {  15.0, -28.0, 3.2 },
                        new[] { -15.0,  28.0, 3.2 },
                        new[] {  15.0,  28.0, 3.2 },
                    },
                    Connectors = new List<ConnectorAccess>
                    {
                        new() { Kind = "shaft", LocalPositionMm = new[] { 0.0, 32.5, 0.0 }, Direction = "+Y", CutoutSizeMm = new[] { 7.0, 7.0 } }
                    }
                }));
            list.Add(new ModuleSpec("actuator.nema17_stepper", "Actuator",
                "NEMA-17 bipolar stepper motor (e.g. 1.7 A/phase, 1.8°/step)",
                "8-24 V", "1.7 A/coil",
                mouserKeyword: "NEMA17 stepper motor 1.7A",
                pins: new[] { P("A1","coil"), P("A2","coil"), P("B1","coil"), P("B2","coil") })
                .With(new ModuleFootprint
                {
                    DxMm = 42.3, DyMm = 42.3, DzMm = 47.0,
                    MountStrategy = "screw-bosses",
                    MountHolesMm = new List<double[]>
                    {
                        new[] { -15.5, -15.5, 3.2 },
                        new[] {  15.5, -15.5, 3.2 },
                        new[] { -15.5,  15.5, 3.2 },
                        new[] {  15.5,  15.5, 3.2 },
                    },
                    Connectors = new List<ConnectorAccess>
                    {
                        new() { Kind = "shaft", LocalPositionMm = new[] { 0.0, 0.0, 23.5 }, Direction = "+Z", CutoutSizeMm = new[] { 6.0, 6.0 } }
                    }
                }));
            list.Add(new ModuleSpec("actuator.servo_sg90", "Actuator",
                "SG90 hobby servo — 9 g, ~1.8 kg·cm, PWM 50 Hz",
                "4.8-6 V", "650 mA stall",
                mouserKeyword: "SG90 servo motor",
                pins: new[] { P("VCC","power_in"), P("GND","gnd"), P("PWM","pwm_in") })
                .With(new ModuleFootprint
                {
                    DxMm = 12.0, DyMm = 23.0, DzMm = 22.0,
                    MountStrategy = "screw-bosses",
                    MountHolesMm = new List<double[]>
                    {
                        new[] { 0.0, -13.5, 2.2 },
                        new[] { 0.0,  13.5, 2.2 },
                    },
                    Connectors = new List<ConnectorAccess>
                    {
                        new() { Kind = "shaft", LocalPositionMm = new[] { 0.0, 6.0, 11.0 }, Direction = "+Z", CutoutSizeMm = new[] { 8.0, 8.0 } }
                    }
                }));
            list.Add(new ModuleSpec("actuator.led_status", "Actuator",
                "Status LED with 330 Ω series resistor",
                "1.8-3.3 V", "10 mA",
                mouserKeyword: "5mm LED kit assorted",
                pins: new[] { P("ANODE","gpio_in"), P("CATHODE","gnd") })
                .With(new ModuleFootprint
                {
                    DxMm = 5.5, DyMm = 5.5, DzMm = 8.0,
                    MountStrategy = "snap-fit",
                    Connectors = new List<ConnectorAccess>
                    {
                        new() { Kind = "led-indicator", LocalPositionMm = new[] { 0.0, 0.0, 4.0 }, Direction = "+Z", CutoutSizeMm = new[] { 5.2, 5.2 } }
                    }
                }));

            // ── Power ──
            list.Add(new ModuleSpec("power.battery_lipo_3s", "Power",
                "3S Li-ion / LiPo pack — 11.1 V nominal, ~2200 mAh",
                "9.0-12.6 V", "5 A continuous",
                mouserKeyword: "3S 11.1V LiPo battery",
                pins: new[] { P("V+","power_out"), P("V-","gnd") })
                .With(new ModuleFootprint
                {
                    DxMm = 35.0, DyMm = 70.0, DzMm = 25.0,
                    MountStrategy = "strap",
                    Connectors = new List<ConnectorAccess>
                    {
                        new() { Kind = "jst-xh", LocalPositionMm = new[] { 0.0, 35.0, 12.0 }, Direction = "+Y", CutoutSizeMm = new[] { 15.0, 8.0 } }
                    }
                }));
            list.Add(new ModuleSpec("power.buck_lm2596", "Power",
                "LM2596 adjustable buck — 4.5-40 V in, 1.5-37 V out, 2 A",
                "4.5-40 V", "2 A out",
                mouserKeyword: "Texas Instruments LM2596",
                pins: new[] { P("VIN+","power_in"), P("VIN-","gnd"), P("VOUT+","power_out"), P("VOUT-","gnd") })
                .With(new ModuleFootprint
                {
                    DxMm = 22.0, DyMm = 43.0, DzMm = 14.0,
                    MountStrategy = "screw-bosses",
                    MountHolesMm = new List<double[]>
                    {
                        new[] { -8.5, -18.0, 3.2 },
                        new[] {  8.5, -18.0, 3.2 },
                        new[] { -8.5,  18.0, 3.2 },
                        new[] {  8.5,  18.0, 3.2 },
                    }
                }));
            list.Add(new ModuleSpec("power.buck_mp1584", "Power",
                "MP1584 mini buck — 4.5-28 V in, 0.8-20 V out, 3 A",
                "4.5-28 V", "3 A out",
                mouserKeyword: "MPS MP1584",
                pins: new[] { P("VIN+","power_in"), P("VIN-","gnd"), P("VOUT+","power_out"), P("VOUT-","gnd") })
                .With(new ModuleFootprint
                {
                    DxMm = 17.0, DyMm = 22.0, DzMm = 4.0,
                    MountStrategy = "double-sided-tape",
                }));

            // ── Comms ──
            list.Add(new ModuleSpec("comms.hc05_bt", "Comms",
                "HC-05 Bluetooth SPP (UART)",
                "3.6-6 V", "30 mA",
                mouserKeyword: "HC-05 Bluetooth module",
                pins: new[] { P("VCC","power_in"), P("GND","gnd"), P("TX","uart_out"), P("RX","uart_in") })
                .With(new ModuleFootprint
                {
                    DxMm = 15.0, DyMm = 28.0, DzMm = 3.0,
                    MountStrategy = "PCB-headers",
                }));
            list.Add(new ModuleSpec("comms.nrf24l01", "Comms",
                "nRF24L01+ 2.4 GHz radio (SPI)",
                "3.3 V (logic 5 V tolerant)", "13 mA peak",
                mouserKeyword: "Nordic nRF24L01+",
                pins: new[] { P("VCC","power_in"), P("GND","gnd"),
                    P("CE","gpio_in"), P("CSN","spi_cs"),
                    P("SCK","spi_sck"), P("MOSI","spi_mosi"), P("MISO","spi_miso"), P("IRQ","gpio_out") })
                .With(new ModuleFootprint
                {
                    DxMm = 15.0, DyMm = 29.0, DzMm = 12.0,
                    MountStrategy = "PCB-headers",
                    Connectors = new List<ConnectorAccess>
                    {
                        new() { Kind = "antenna", LocalPositionMm = new[] { 0.0, 14.0, 10.0 }, Direction = "+Z", CutoutSizeMm = new[] { 12.0, 4.0 } }
                    }
                }));

            return list;
        }

        private static PinSpec P(string name, string kind) => new PinSpec { Name = name, Kind = kind };
    }

    public class ModuleSpec
    {
        public string Id { get; set; } = "";
        public string Category { get; set; } = "";   // MCU / MotorDriver / StepperDriver / Sensor / Actuator / Power / Comms
        public string Description { get; set; } = "";
        public string OperatingVoltage { get; set; } = "";
        public string MaxCurrent { get; set; } = "";
        /// <summary>Search keyword used by <see cref="StratumPartsCatalog"/> against the Mouser API.</summary>
        public string MouserKeyword { get; set; } = "";
        public List<PinSpec> Pins { get; set; } = new();
        /// <summary>Physical footprint used by the mechanical agent to reserve volume + generate mounting bosses, screw holes, and connector cutouts.</summary>
        public ModuleFootprint? Footprint { get; set; }

        public ModuleSpec() { }
        public ModuleSpec(string id, string category, string description, string voltage, string current, string mouserKeyword, IEnumerable<PinSpec> pins)
        {
            Id = id; Category = category; Description = description;
            OperatingVoltage = voltage; MaxCurrent = current; MouserKeyword = mouserKeyword;
            Pins = pins.ToList();
        }

        public ModuleSpec With(ModuleFootprint footprint)
        {
            Footprint = footprint;
            return this;
        }
    }

    public class PinSpec
    {
        public string Name { get; set; } = "";
        /// <summary>Kind tag — gpio, pwm, gnd, power_in/out, i2c_sda/scl, spi_*, motor_*, analog, etc.</summary>
        public string Kind { get; set; } = "";
    }

    /// <summary>
    /// Physical footprint of an electronics module — used by the mechanical agent to reserve
    /// volume inside the enclosure, generate mounting bosses/screw holes, and cut connector access
    /// through enclosure walls. Dimensions in millimetres, local frame: module body axis-aligned
    /// with X/Y in the PCB plane and Z normal to the PCB (positive = component side).
    /// </summary>
    public class ModuleFootprint
    {
        public double DxMm { get; set; }
        public double DyMm { get; set; }
        public double DzMm { get; set; }
        /// <summary>Mount-hole positions in module-local frame as [x, y, diameterMm]. Empty if PCB-headers or wire-mount.</summary>
        public List<double[]> MountHolesMm { get; set; } = new();
        public List<ConnectorAccess> Connectors { get; set; } = new();
        /// <summary>"screw-bosses" | "snap-fit" | "double-sided-tape" | "DIN-rail" | "PCB-headers" | "strap" | "wire-mount" | "shaft-bearing".</summary>
        public string MountStrategy { get; set; } = "screw-bosses";
    }

    /// <summary>One physical connector/protrusion that needs to emerge from an enclosure wall.</summary>
    public class ConnectorAccess
    {
        /// <summary>"usb-c" | "usb-micro" | "usb-a" | "jst-xh" | "jst-ph" | "barrel-jack" | "rj45" | "screw-terminal" | "switch-toggle" | "led-indicator" | "shaft" | "antenna".</summary>
        public string Kind { get; set; } = "";
        /// <summary>Connector centre, in module-local frame [x, y, z].</summary>
        public double[] LocalPositionMm { get; set; } = new double[] { 0, 0, 0 };
        /// <summary>Face of the module body the connector emerges from: "+X" | "-X" | "+Y" | "-Y" | "+Z" | "-Z".</summary>
        public string Direction { get; set; } = "+Z";
        /// <summary>Rectangular opening size required in the enclosure wall [dx, dy] (along the two axes perpendicular to Direction). For circular cutouts (e.g. LEDs, shafts), use the diameter for both.</summary>
        public double[] CutoutSizeMm { get; set; } = new double[] { 0, 0 };
    }

    // ── Electronics Agent output schema ──

    public class StratumElectronicsDesign
    {
        public string Summary { get; set; } = "";
        public List<ElectronicsModuleInstance> Modules { get; set; } = new();
        public List<ElectronicsWire> Wires { get; set; } = new();
        public List<string> Assumptions { get; set; } = new();
        public List<string> OpenQuestions { get; set; } = new();
    }

    public class ElectronicsModuleInstance
    {
        public string InstanceId { get; set; } = ""; // e.g. "u1", "drv_left"
        public string ModuleId { get; set; } = "";   // FK into StratumModuleLibrary
        public string Role { get; set; } = "";       // human-readable role in this design
    }

    public class ElectronicsWire
    {
        public string FromInstance { get; set; } = "";
        public string FromPin { get; set; } = "";
        public string ToInstance { get; set; } = "";
        public string ToPin { get; set; } = "";
        public string Signal { get; set; } = ""; // e.g. "PWM_left", "GND", "VCC_5V", "I2C_SDA"
    }

    /// <summary>
    /// Spatial placement of every electronics module inside the enclosure. Produced by the
    /// Electronics Agent's layout phase AFTER the wiring design has been approved, and consumed
    /// by the Mechanical Agent to (a) reserve volume in the assembly blueprint and (b) generate
    /// real bosses, screw holes, and connector cutouts on the parts that host each module.
    /// </summary>
    public class StratumElectronicsLayout
    {
        public string OriginConvention { get; set; } = "";
        public List<ElectronicsModulePlacement> Placements { get; set; } = new();
        public List<string> Assumptions { get; set; } = new();
        public List<string> OpenQuestions { get; set; } = new();
    }

    public class ElectronicsModulePlacement
    {
        public string InstanceId { get; set; } = "";
        public string ModuleId { get; set; } = "";
        public string Role { get; set; } = "";
        public double[] WorldPositionMm { get; set; } = new double[] { 0, 0, 0 };
        public double[] WorldRotationDeg { get; set; } = new double[] { 0, 0, 0 };
        /// <summary>Snapshot of the module's footprint from <see cref="StratumModuleLibrary"/> at placement time, so the layout stays internally consistent even if the library is later updated.</summary>
        public ModuleFootprint Footprint { get; set; } = new();
        /// <summary>Subtask title of the mechanical part that physically carries this module. Must match a planner mechanical subtask title verbatim.</summary>
        public string HostingPart { get; set; } = "";
        public string Reasoning { get; set; } = "";
    }

    public class StratumBom
    {
        public List<BomLine> Lines { get; set; } = new();
        public string Notes { get; set; } = "";
    }

    public class BomLine
    {
        public string ModuleId { get; set; } = "";
        public string Role { get; set; } = "";
        public int Quantity { get; set; }
        /// <summary>Live distributor candidates, populated by <see cref="StratumPartsCatalog"/>.</summary>
        public List<DistributorPart> DistributorCandidates { get; set; } = new();
    }

    public class DistributorPart
    {
        public string Distributor { get; set; } = ""; // "Mouser"
        public string ManufacturerPartNumber { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Description { get; set; } = "";
        public string ProductDetailUrl { get; set; } = "";
        public string DataSheetUrl { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public string PriceQty1 { get; set; } = "";   // currency string, e.g. "$3.45"
        public string Availability { get; set; } = ""; // "5,231 In Stock"
    }
}
