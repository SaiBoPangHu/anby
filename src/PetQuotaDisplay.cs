using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AnbyPet
{
    internal struct FiveHourQuota
    {
        internal readonly string Name, Remaining, Note;
        internal readonly double? Percent;

        internal FiveHourQuota(string name, string remaining, string note, double? percent)
        {
            Name = name;
            Remaining = remaining;
            Note = note;
            Percent = percent;
        }
    }

    internal sealed class PetQuotaDisplay
    {
        internal readonly FiveHourQuota[] FiveHourRows;
        internal readonly string[] WeekRows;
        internal readonly double? BurgerPercent;

        internal PetQuotaDisplay(PanelData readings, DateTimeOffset now)
        {
            var fiveHours = new List<FiveHourQuota>();
            var weeks = new List<string>();
            double? burgerPercent = null;
            AddProvider("Codex", readings.Providers.Codex, readings.Codex, readings.CodexStatus,
                now, fiveHours, weeks, ref burgerPercent);
            AddProvider("Antigravity", readings.Providers.Antigravity, readings.Antigravity, readings.AntigravityStatus,
                now, fiveHours, weeks, ref burgerPercent);
            FiveHourRows = fiveHours.ToArray();
            WeekRows = weeks.ToArray();
            BurgerPercent = burgerPercent;
        }

        private static void AddProvider(string name, bool running, QuotaSnapshot snapshot, string status,
            DateTimeOffset now, List<FiveHourQuota> fiveHours, List<string> weeks, ref double? burgerPercent)
        {
            int fiveHourCount = fiveHours.Count;
            int weekCount = weeks.Count;
            if (running && snapshot != null)
            {
                foreach (var window in snapshot.Windows.OrderBy(item => item.Name != null &&
                    item.Name.StartsWith("Gemini ·", StringComparison.Ordinal) ? 0 : 1))
                {
                    if (window.Period == QuotaPeriod.Other) continue;
                    bool hasReset = window.Reset.HasValue && window.Reset.Value.Year > 1970;
                    bool expired = hasReset && window.Reset.Value <= now;
                    bool stale = !expired && !string.IsNullOrWhiteSpace(status);
                    string reset = hasReset && !expired ? Board.QuotaTime(window.Reset.Value) + " 重置" : "";
                    string amount = expired ? "待更新" : Amount(window);
                    if (window.Period == QuotaPeriod.Weekly)
                    {
                        string weekly = expired ? amount : (stale ? "上次剩 " : "剩 ") + amount;
                        weeks.Add(name + " · " + window.Name + " · " + weekly + (reset.Length == 0 ? "" : " · " + reset));
                        continue;
                    }
                    var percent = expired ? null : window.RemainingPercent;
                    bool chosen = !burgerPercent.HasValue && percent.HasValue;
                    if (chosen) burgerPercent = percent;
                    string note = string.Join(" · ", new[] { chosen ? "汉堡" : "", stale ? "上次读数" : "", reset }
                        .Where(part => part.Length != 0));
                    fiveHours.Add(new FiveHourQuota(FiveHourName(name, window.Name), amount, note, percent));
                }
            }

            string waiting = !running ? "未运行" : (string.IsNullOrWhiteSpace(status) ? "暂无读数" : status);
            if (fiveHours.Count == fiveHourCount)
                fiveHours.Add(new FiveHourQuota(name, !running ? "未运行" : "暂无读数", running ? status : null, null));
            if (weeks.Count == weekCount) weeks.Add(name + " · " + waiting);
        }

        private static string FiveHourName(string provider, string window)
        {
            const string suffix = " · 5 小时";
            if (string.IsNullOrWhiteSpace(window) || window == "5 小时") return provider;
            if (window.EndsWith(suffix, StringComparison.Ordinal)) window = window.Substring(0, window.Length - suffix.Length);
            return provider + " · " + window;
        }

        private static string Amount(QuotaWindow window)
        {
            if (window.RemainingPercent.HasValue)
            {
                double percent = window.RemainingPercent.Value;
                return percent > 0 && percent < 0.01
                    ? "<0.01%" : percent.ToString("0.##", CultureInfo.CurrentCulture) + "%";
            }
            return window.RemainingAmount.HasValue
                ? window.RemainingAmount.Value.ToString("0.##", CultureInfo.CurrentCulture) : "—";
        }
    }
}
