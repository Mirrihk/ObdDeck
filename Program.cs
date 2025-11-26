using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using InTheHand.Bluetooth;

class Program
{
    // Veepeak often advertises as "OBDCheck BLE" – adjust if yours is different
    const string NamePrefix = "OBD";

    // Common custom ELM327 BLE UUIDs – we can change these later if needed
    static readonly Guid ServiceUuid = Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb");
    static readonly Guid NotifyUuid = Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb");
    static readonly Guid WriteUuid = Guid.Parse("0000fff2-0000-1000-8000-00805f9b34fb");

    static async Task Main()
    {
        Console.WriteLine("=== OBD Deck ===");
        Console.WriteLine("Make sure: Veepeak is plugged in, car ignition ON, Bluetooth enabled.\n");

        // 1) Scan for devices and pick the one whose name starts with "OBD"
        var options = new RequestDeviceOptions
        {
            AcceptAllDevices = true
        };

        Console.WriteLine("Scanning for Bluetooth devices...");
        var devices = await Bluetooth.ScanForDevicesAsync(options);

        if (devices == null || devices.Count == 0)
        {
            Console.WriteLine("No Bluetooth devices found. Is Bluetooth enabled?");
            return;
        }

        // Try to find our OBD adapter by name prefix
        var device = devices
            .FirstOrDefault(d => !string.IsNullOrEmpty(d.Name) &&
                                 d.Name.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase));

        if (device == null)
        {
            Console.WriteLine("Could not find a device whose name starts with 'OBD'.");
            Console.WriteLine("Devices seen:");
            foreach (var d in devices)
                Console.WriteLine($" - \"{d.Name}\"  ({d.Id})");
            Console.WriteLine("\nUpdate NamePrefix in the code to match your adapter name.");
            return;
        }

        Console.WriteLine($"Selected device: {device.Name}");


        // 2) Connect to GATT
        var gatt = device.Gatt;
        Console.WriteLine("Connecting to GATT...");
        await gatt.ConnectAsync();

        // 3) Get service + characteristics
        var service = await gatt.GetPrimaryServiceAsync(ServiceUuid);
        if (service == null)
        {
            Console.WriteLine("OBD service not found.");
            return;
        }

        var notifyChar = await service.GetCharacteristicAsync(NotifyUuid);
        var writeChar = await service.GetCharacteristicAsync(WriteUuid);

        if (notifyChar == null || writeChar == null)
        {
            Console.WriteLine("OBD characteristics not found.");
            return;
        }

        // 4) Subscribe to responses
        notifyChar.CharacteristicValueChanged += (s, e) =>
        {
            var bytes = e.Value;
            if (bytes == null || bytes.Length == 0)
                return;

            var text = Encoding.ASCII.GetString(bytes);

            foreach (var raw in text.Split('\r', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim(' ', '\n', '>', '\0');
                if (line.Length == 0) continue;

                // Uncomment to see everything coming back:
                // Console.WriteLine("RAW: " + line);

                ParseObdLine(line);
            }
        };
        await notifyChar.StartNotificationsAsync();

        // 5) ELM327 init + helper send method
        async Task Send(string cmd)
        {
            var bytes = Encoding.ASCII.GetBytes(cmd + "\r");
            // BLE write; "with response" is safest. If it errors, switch to WithoutResponse.
            await writeChar.WriteValueWithResponseAsync(bytes);
            // await writeChar.WriteValueWithoutResponseAsync(bytes);
        }

        Console.WriteLine("Initializing ELM327...");
        await Send("ATZ");   // reset
        await Task.Delay(500);
        await Send("ATE0");  // echo off
        await Send("ATL0");  // linefeeds off
        await Send("ATS0");  // spaces off
        await Send("ATH0");  // headers off
        await Send("ATSP0"); // auto protocol

        Console.WriteLine("Init complete. Polling every second. Ctrl+C to exit.\n");

        // 6) Poll loop
        while (true)
        {
            await Send("010C"); // RPM
            await Send("010D"); // Speed
            await Send("0105"); // Coolant temp
            await Task.Delay(1000);
        }
    }

    static void ParseObdLine(string line)
    {
        // Clean up and normalize
        line = line.Replace("\n", " ").Replace("\t", " ").Trim();

        // Two common formats:
        //  1) "41 0C 1A F8"
        //  2) "410C 1A F8"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return;

        string modePid;
        int dataStartIndex;

        if (parts[0].Length == 4 && parts[0].StartsWith("41", StringComparison.Ordinal))
        {
            // "410C 1A F8"
            modePid = parts[0];
            dataStartIndex = 1;
        }
        else if (parts[0] == "41" && parts.Length >= 3)
        {
            // "41 0C 1A F8"
            modePid = parts[0] + parts[1]; // "41" + "0C" -> "410C"
            dataStartIndex = 2;
        }
        else
        {
            return;
        }

        // Need at least one data byte
        if (parts.Length <= dataStartIndex)
            return;

        try
        {
            byte A = Convert.ToByte(parts[dataStartIndex], 16);
            byte B = (parts.Length > dataStartIndex + 1)
                ? Convert.ToByte(parts[dataStartIndex + 1], 16)
                : (byte)0;

            switch (modePid)
            {
                case "410C": // RPM
                    int rpm = ((A * 256) + B) / 4;
                    Console.WriteLine($"RPM: {rpm}");
                    break;

                case "410D": // Speed km/h
                    int speed = A;
                    Console.WriteLine($"Speed: {speed} km/h");
                    break;

                case "4105": // Coolant temp
                    int temp = A - 40;
                    Console.WriteLine($"Coolant: {temp} °C");
                    break;

                default:
                    // Uncomment if you want to see other PIDs:
                    // Console.WriteLine($"RAW PID {modePid}: {line}");
                    break;
            }
        }
        catch
        {
            // Ignore bad frames for now
        }
    }
}
