using System;
using System.Collections.Generic;
using Google.Protobuf;
using GPConf.Utilities;

namespace GPConf.UI.StaticWidgets;

public static class PointsPresets
{
    public sealed record Preset(string DisplayName, Func<PointsScoringRules> Create);

    public static readonly List<Preset> All = new()
    {
        new Preset(
            "F1 2025 Grand Prix",
            () => new PointsScoringRules
            {
                Id = CCUtils.CreateUniqueId(),
                Name = "F1 Grand Prix",
                Score = { 25, 18, 15, 12, 10, 8, 6, 4, 2, 1 },
                ConfCupMultiplier = 1.0f
            }
        ),
        new Preset(
            "F1 2025 Sprint",
            () => new PointsScoringRules
            {
                Id = CCUtils.CreateUniqueId(),
                Name = "F1 2025 (Sprint)",
                Score = { 8, 7, 6, 5, 4, 3, 2, 1 },
                ConfCupMultiplier = 0.5f
            }
        ),
        new Preset(
            "F1 2003-2009",
            () => new PointsScoringRules
            {
                Id = CCUtils.CreateUniqueId(),
                Name = "F1 2003-2009",
                Score = { 10, 8, 6, 5, 4, 3, 2, 1 },
                ConfCupMultiplier = 1.0f
            }
        ),
        new Preset(
            "F1 1991-2002",
            () => new PointsScoringRules
            {
                Id = CCUtils.CreateUniqueId(),
                Name = "F1 1991-2002",
                Score = { 10, 6, 4, 3, 2, 1 },
                ConfCupMultiplier = 1.0f
            }
        ),
        new Preset(
            "F1 1961-1990",
            () => new PointsScoringRules
            {
                Id = CCUtils.CreateUniqueId(),
                Name = "F1 1961-1990",
                Score = { 9, 6, 4, 3, 2, 1 },
                ConfCupMultiplier = 1.0f
            }
        )
    };
}