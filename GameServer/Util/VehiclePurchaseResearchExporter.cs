using System;
using System.Globalization;
using System.IO;
using System.Text;
using Shared.Util;

namespace GameServer.Util
{
    internal static class VehiclePurchaseResearchExporter
    {
        private static readonly object Sync = new object();
        private const string FileName = "VehiclePurchaseResearch.csv";

        public static void LogPurchase(
            ulong characterId,
            uint carInstanceId,
            int runtimeIndex,
            uint carType,
            string vehicleName,
            uint grade,
            uint color)
        {
            try
            {
                var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "Catalogs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, FileName);

                lock (Sync)
                {
                    if (!File.Exists(path))
                    {
                        File.WriteAllText(
                            path,
                            "PurchasedAtUtc,CharacterId,CarInstanceId,RuntimeIndex,VehicleId_CarType,VehicleName,Grade,Color,KeyItemIdToFill" + Environment.NewLine,
                            new UTF8Encoding(true));
                    }

                    var line = string.Join(",", new[]
                    {
                        Csv(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
                        characterId.ToString(CultureInfo.InvariantCulture),
                        carInstanceId.ToString(CultureInfo.InvariantCulture),
                        runtimeIndex.ToString(CultureInfo.InvariantCulture),
                        carType.ToString(CultureInfo.InvariantCulture),
                        Csv(vehicleName),
                        grade.ToString(CultureInfo.InvariantCulture),
                        color.ToString(CultureInfo.InvariantCulture),
                        string.Empty
                    });

                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(true));
                }

                Log.Info(
                    "Vehicle purchase research: CarId={0} RuntimeIndex={1} VehicleId/CarType={2} Vehicle='{3}' Grade=V{4} -> Logs/Catalogs/{5}",
                    carInstanceId,
                    runtimeIndex,
                    carType,
                    vehicleName,
                    grade,
                    FileName);
            }
            catch (Exception ex)
            {
                Log.Warning("Vehicle purchase research log failed: {0}", ex.Message);
            }
        }

        private static string Csv(string value)
        {
            value = value ?? string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
