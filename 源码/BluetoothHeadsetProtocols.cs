using System;
using System.Collections.Generic;
using System.Linq;

namespace PeripheralBattery {
    internal static class BluetoothHeadsetProtocols {
        static readonly Guid Spp = new Guid("00001101-0000-1000-8000-00805f9b34fb");
        static readonly Guid SonyMdr = new Guid("956c7b26-d49a-4ba8-b03f-b17d393cb6e2");
        static readonly Guid SonyMdrLegacy = new Guid("96cc203e-5068-46ad-b32d-e316f5e069ba");
        static readonly Guid SamsungBuds = new Guid("2e73a4ad-332d-41fc-90e2-16bef06523f2");
        static readonly Guid BoseAccessory = new Guid("00000000-deca-fade-deca-deafdecacaff");
        static readonly Guid AirohaRace = new Guid("00000000-0000-0000-0099-aabbccddeeff");
        static readonly Guid FastPair = new Guid("df21fe2c-2515-4fdb-8886-f12c4d67927c");
        static readonly Guid SennheiserGaia = new Guid("a2129ff3-081b-4c45-8afe-469d9c4842ec");
        static readonly Guid VivoGaia = new Guid("00000837-d102-11e1-9b23-00025b00a5a5");
        static readonly Guid NothingControl = new Guid("aeac4a03-dff5-498f-843a-34487cf133eb");
        static readonly Guid SoundcoreSpaceOne = new Guid("0cf12d31-fac3-4553-bd80-d6832e7b3035");
        static readonly HashSet<string> ListedModels = new HashSet<string>(StringComparer.Ordinal) {
            "wf1000xm6","sonywf1000xm6","wfc710n","sonywfc710n","wh1000xm6","sonywh1000xm6",
            "quietcomfortultraearbuds","bosequietcomfortultraearbuds","boseqcultraearbuds",
            "quietcomfortultraheadphones","bosequietcomfortultraheadphones","boseqcultraheadphones",
            "airpodspro","airpodspro3","airpodspro3rdgeneration","galaxybuds4pro","samsunggalaxybuds4pro","smr640",
            "pi8","bwpi8","bowerswilkinspi8","eahaz100","technicseahaz100","redmibuds8pro","红米buds8pro",
            "powerbeatspro2","beatspowerbeatspro2","jbltourpro3","tourpro3","huaweifreebudspro5","freebudspro5",
            "vivotws5","huaweifreeclip2","freeclip2","moondroppudding","pudding","水月雨布丁",
            "qcya30","momentum5","momentum5wireless","sennheisermomentum5","sennheisermomentum5wireless","m5aebt",
            "openrunpro2","shokzopenrunpro2","openrunpro2byshokz","openfit2","shokzopenfit2","openfit2byshokz",
            "airpods4","airpods4anc","airpods4withanc","airpods4thgeneration","airpods4thgenerationwithanc",
            "galaxybuds3pro","samsunggalaxybuds3pro","smr630",
            "momentumtw4","momentumtruewireless4","sennheisermomentumtw4","sennheisermomentumtruewireless4","mtw4",
            "xiaomibuds5pro","小米buds5pro","edifierneobudspro2","neobudspro2","漫步者neobudspro2",
            "linkbudss","sonylinkbudss","wfls900n","sonywfls900n","nothingear","nothingear2024",
            "soundcorespaceone","ankersoundcorespaceone","soundcorespaceonea3035",
            "boseultraopenearbuds","ultraopenearbuds","openswimpro2","shokzopenswimpro2","openswimpro2byshokz"
        };

        static string Normalize(string name) {
            string text = (name ?? "").Trim();
            if (text.StartsWith("LE_",StringComparison.OrdinalIgnoreCase)) text = text.Substring(3);
            if (text.EndsWith(")",StringComparison.Ordinal)) {
                int marker = text.LastIndexOf('(');
                if (marker > 0 && text.Length-marker == 6 && text.Substring(marker+1,4).All(Uri.IsHexDigit))
                    text = text.Substring(0,marker);
            }
            return new string(text.Where(Char.IsLetterOrDigit).Select(Char.ToLowerInvariant).ToArray());
        }

        internal static bool IsListed(string name) { return ListedModels.Contains(Normalize(name)); }

        sealed class Sample {
            public BatteryState State;
            public DateTime Until;
        }
        static readonly Dictionary<string,Sample> Samples = new Dictionary<string,Sample>();

        internal static BatteryState Read(string key, string name, ulong address,
                List<BluetoothHeadsetBattery.DeviceNode> nodes, bool force) {
            string model = Normalize(name);
            if (!ListedModels.Contains(model)) return null;
            DateTime now = DateTime.UtcNow;
            Sample saved;
            if (!force && Samples.TryGetValue(key,out saved) && now < saved.Until) return Copy(saved.State);
            BatteryState current = ReadProtocol(model,address,nodes);
            foreach (string expired in Samples.Where(pair => pair.Value.Until <= now).Select(pair => pair.Key).ToArray())
                Samples.Remove(expired);
            Samples[key] = new Sample { State=current, Until=now.AddSeconds(current != null ? 115 : 25) };
            return Copy(current);
        }

        static BatteryState Copy(BatteryState state) {
            return state == null ? null : new BatteryState {
                Percent=state.Percent, Charging=state.Charging, Estimated=state.Estimated, Detail=state.Detail
            };
        }

        static BatteryState ReadProtocol(string model, ulong address, List<BluetoothHeadsetBattery.DeviceNode> nodes) {
            switch (model) {
                case "wf1000xm6": case "sonywf1000xm6":
                    return RfcommBatteryLink.Use(address,SonyMdr,link => BluetoothHeadsetFrames.ReadSony(link,true,9));
                case "wfc710n": case "sonywfc710n":
                    return RfcommBatteryLink.Use(address,SonyMdrLegacy,link => BluetoothHeadsetFrames.ReadSony(link,false,1)) ??
                        RfcommBatteryLink.Use(address,SonyMdr,link => BluetoothHeadsetFrames.ReadSony(link,true,1));
                case "wh1000xm6": case "sonywh1000xm6":
                    return RfcommBatteryLink.Use(address,SonyMdr,link => BluetoothHeadsetFrames.ReadSony(link,true,0));
                case "linkbudss": case "sonylinkbudss": case "wfls900n": case "sonywfls900n":
                    return RfcommBatteryLink.Use(address,SonyMdr,link => BluetoothHeadsetFrames.ReadSony(link,true,9)) ??
                        RfcommBatteryLink.Use(address,SonyMdrLegacy,link => BluetoothHeadsetFrames.ReadSony(link,false,1));
                case "galaxybuds4pro": case "samsunggalaxybuds4pro": case "smr640":
                case "galaxybuds3pro": case "samsunggalaxybuds3pro": case "smr630":
                    return RfcommBatteryLink.Use(address,SamsungBuds,BluetoothHeadsetFrames.ReadSamsung);
                case "quietcomfortultraearbuds": case "bosequietcomfortultraearbuds": case "boseqcultraearbuds":
                    return ReadBose(address,0x4072);
                case "quietcomfortultraheadphones": case "bosequietcomfortultraheadphones": case "boseqcultraheadphones":
                    return ReadBose(address,0x4066);
                case "boseultraopenearbuds": case "ultraopenearbuds":
                    return ReadBose(address,0x4068);
                case "huaweifreeclip2": case "freeclip2": case "huaweifreebudspro5": case "freebudspro5":
                    return RfcommBatteryLink.Use(address,Spp,BluetoothHeadsetFrames.ReadHuawei);
                case "eahaz100": case "technicseahaz100":
                    return RfcommBatteryLink.Use(address,AirohaRace,BluetoothHeadsetFrames.ReadRaceEarbuds) ??
                        RfcommBatteryLink.Use(address,Spp,BluetoothHeadsetFrames.ReadRaceEarbuds);
                case "jbltourpro3": case "tourpro3":
                    return RfcommBatteryLink.Use(address,AirohaRace,BluetoothHeadsetFrames.ReadRaceEarbuds) ??
                        RfcommBatteryLink.Use(address,FastPair,BluetoothHeadsetFrames.ReadFastPair);
                case "pi8": case "bwpi8": case "bowerswilkinspi8": case "redmibuds8pro": case "红米buds8pro":
                case "edifierneobudspro2": case "neobudspro2": case "漫步者neobudspro2":
                    return RfcommBatteryLink.Use(address,FastPair,BluetoothHeadsetFrames.ReadFastPair);
                case "momentum5": case "momentum5wireless": case "sennheisermomentum5":
                case "sennheisermomentum5wireless": case "m5aebt":
                    return RfcommBatteryLink.Use(address,SennheiserGaia,BluetoothHeadsetFrames.ReadMomentum,9000);
                case "momentumtw4": case "momentumtruewireless4": case "sennheisermomentumtw4":
                case "sennheisermomentumtruewireless4": case "mtw4":
                    return RfcommBatteryLink.Use(address,SennheiserGaia,BluetoothHeadsetFrames.ReadMomentumEarbuds);
                case "moondroppudding": case "pudding": case "水月雨布丁":
                    return RfcommBatteryLink.Use(address,Spp,BluetoothHeadsetFrames.ReadPudding);
                case "airpodspro": case "airpodspro3": case "airpodspro3rdgeneration":
                    return BluetoothHeadsetLowEnergy.ReadAppleEarbuds(address,nodes,0x2720);
                case "powerbeatspro2": case "beatspowerbeatspro2":
                    return BluetoothHeadsetLowEnergy.ReadAppleEarbuds(address,nodes,0x1D20);
                case "airpods4": case "airpods4anc": case "airpods4withanc":
                case "airpods4thgeneration": case "airpods4thgenerationwithanc":
                    return BluetoothHeadsetLowEnergy.ReadAppleEarbuds(address,nodes,0x1B20);
                case "nothingear": case "nothingear2024":
                    return RfcommBatteryLink.Use(address,NothingControl,SupplementalHeadsetFrames.ReadNothingEar);
                case "soundcorespaceone": case "ankersoundcorespaceone": case "soundcorespaceonea3035":
                    return RfcommBatteryLink.Use(address,SoundcoreSpaceOne,SupplementalHeadsetFrames.ReadSpaceOne);
                case "vivotws5":
                    return RfcommBatteryLink.Use(address,VivoGaia,BluetoothHeadsetFrames.ReadVivo) ??
                        BluetoothHeadsetLowEnergy.ReadVivo(nodes);
                case "qcya30":
                    return BluetoothHeadsetLowEnergy.ReadQcy(nodes);
                default:
                    return null;
            }
        }

        static BatteryState ReadBose(ulong address, int product) {
            return RfcommBatteryLink.Use(address,Spp,link => BluetoothHeadsetFrames.ReadBose(link,product)) ??
                RfcommBatteryLink.Use(address,BoseAccessory,link => BluetoothHeadsetFrames.ReadBose(link,product));
        }
    }
}
