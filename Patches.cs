using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAI_OBS_Telemetry
{
    [HarmonyPatch(typeof(scrController), "Start")]
    public static class ControllerStartPatch
    {
        public static void Prefix()
        {
            Main.Handler.Log("Controller start");
        }

        public static void Postfix()
        {
            Main.Handler.Log("Controller started");

            if (Main.Settings.EnableFeature)
            {
                Main.Handler.Log(
                    $"Feature enabled, example value: " +
                    $"{Main.Settings.ExampleValue}"
                );
            }

            TelemetryJsonWriter.Reset();
        }
    }

    [HarmonyPatch(typeof(scrController), "Update")]
    public static class ControllerTelemetryPatch
    {
        public static void Postfix(object __instance)
        {
            TelemetryJsonWriter.Update(__instance);
        }
    }

    internal static class TelemetryJsonWriter
    {
        private static readonly string OutputPath =
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "ADOFAI_OBS_Telemetry.json"
            );

        private static int lastScanFrame = -1;
        private static string lastJson = "";

        /*
         * 有効な譜面状態の直前値を保持する。
         *
         * クリア後やシーン遷移中に
         * ADOFAI側の内部値が初期化されても、
         * JSONを変な値で上書きしないため。
         */
        private static TelemetryData lastValidData = null;

        private static string lastMapKey = "";

        private static readonly Regex RichTextRegex =
            new Regex(
                "<.*?>",
                RegexOptions.Compiled
            );

        private static readonly Regex PercentRegex =
            new Regex(
                @"(-?\d+(?:\.\d+)?)\s*%",
                RegexOptions.Compiled
            );

        public static void Reset()
        {
            lastScanFrame = -1;

            /*
             * lastJson / lastValidData は残す。
             *
             * クリア後にControllerが作り直されても
             * 最後の正常な譜面情報を保持する。
             */

            Main.Handler.Log(
                "JSON output path: " +
                OutputPath
            );
        }

        public static void Update(
            object controller
        )
        {
            if (controller == null)
                return;

            /*
             * 約4回/秒程度で更新。
             * OBS側の250ms fetchとほぼ同程度。
             */
            if (
                Time.frameCount -
                lastScanFrame <
                15
            )
            {
                return;
            }

            lastScanFrame =
                Time.frameCount;

            try
            {
                TelemetryData data =
                    ReadTelemetry(
                        controller
                    );

                /*
                 * 有効な譜面状態以外では
                 * JSONを書き換えない。
                 */
                if (!data.IsValidMap)
                {
                    return;
                }

                string mapKey =
                    CreateMapKey(
                        data
                    );

                /*
                 * 新しい譜面なら
                 * 前曲のキャッシュを捨てる。
                 */
                if (
                    !string.IsNullOrEmpty(
                        mapKey
                    ) &&
                    mapKey != lastMapKey
                )
                {
                    lastValidData = null;
                    lastMapKey = mapKey;
                }

                /*
                 * 同じ譜面中に
                 * 一瞬だけ値が取得できなかった場合、
                 * 直前値で補完する。
                 */
                if (lastValidData != null)
                {
                    MergePreviousValues(
                        data,
                        lastValidData
                    );
                }

                /*
                 * titleがUIから取れない場合。
                 */
                if (
                    string.IsNullOrWhiteSpace(
                        data.Title
                    )
                )
                {
                    if (
                        !string.IsNullOrWhiteSpace(
                            data.Artist
                        ) &&
                        !string.IsNullOrWhiteSpace(
                            data.Song
                        )
                    )
                    {
                        data.Title =
                            data.Artist +
                            " - " +
                            data.Song;
                    }
                    else if (
                        !string.IsNullOrWhiteSpace(
                            data.Song
                        )
                    )
                    {
                        data.Title =
                            data.Song;
                    }
                }

                /*
                 * progressText は
                 * UI文字列ではなく内部値から生成。
                 */
                if (data.Progress.HasValue)
                {
                    data.ProgressText =
                        data.Progress.Value.ToString(
                            "0.##",
                            CultureInfo.InvariantCulture
                        ) +
                        "%";
                }
                else
                {
                    data.ProgressText = "";
                }

                /*
                 * mapTimeはmapDurationを超えないようにする。
                 */
                if (
                    data.MapTime.HasValue &&
                    data.MapDuration.HasValue
                )
                {
                    double mapTime =
                        data.MapTime.Value;

                    double mapDuration =
                        data.MapDuration.Value;

                    if (mapTime < 0)
                    {
                        mapTime = 0;
                    }

                    if (
                        mapDuration >= 0 &&
                        mapTime > mapDuration
                    )
                    {
                        mapTime =
                            mapDuration;
                    }

                    data.MapTime =
                        mapTime;
                }

                lastValidData =
                    data.Clone();

                string json =
                    CreateJson(
                        data
                    );

                if (json == lastJson)
                    return;

                File.WriteAllText(
                    OutputPath,
                    json,
                    new UTF8Encoding(false)
                );

                lastJson = json;
            }
            catch (Exception ex)
            {
                Main.Handler.Log(
                    "JSON output error: " +
                    ex
                );
            }
        }

        private static TelemetryData ReadTelemetry(
            object controller
        )
        {
            TelemetryData data =
                new TelemetryData();

            /*
             * =====================================
             * UI FALLBACK
             * =====================================
             */

            string title =
                ReadTextMember(
                    controller,
                    "txtLevelName"
                );

            string progressText =
                ReadTextMember(
                    controller,
                    "txtPercent"
                );

            if (
                !string.IsNullOrEmpty(
                    title
                )
            )
            {
                title =
                    RichTextRegex.Replace(
                        title,
                        ""
                    );
            }

            data.Title =
                title ?? "";

            data.ProgressText =
                progressText ?? "";

            /*
             * =====================================
             * PROGRESS
             * =====================================
             */

            object percentValue =
                ReadMember(
                    controller,
                    "percentComplete"
                );

            if (
                TryGetDouble(
                    percentValue,
                    out double internalProgress
                )
            )
            {
                /*
                 * percentComplete は通常0～1。
                 */
                if (
                    internalProgress >= 0 &&
                    internalProgress <= 1.5
                )
                {
                    data.Progress =
                        internalProgress *
                        100.0;
                }
                else
                {
                    data.Progress =
                        internalProgress;
                }

                if (
                    data.Progress.HasValue &&
                    data.Progress.Value > 100
                )
                {
                    data.Progress =
                        100;
                }

                if (
                    data.Progress.HasValue &&
                    data.Progress.Value < 0
                )
                {
                    data.Progress =
                        0;
                }
            }
            else
            {
                data.Progress =
                    ParsePercent(
                        progressText
                    );
            }

            /*
             * =====================================
             * CURRENT TILE
             * =====================================
             */

            object seqValue =
                ReadMember(
                    controller,
                    "currentSeqID"
                );

            if (
                TryGetInt(
                    seqValue,
                    out int currentSeq
                )
            )
            {
                data.CurrentTile =
                    currentSeq;
            }
            else
            {
                object floorIdValue =
                    ReadMember(
                        controller,
                        "currentFloorID"
                    );

                if (
                    TryGetInt(
                        floorIdValue,
                        out int currentFloorId
                    )
                )
                {
                    data.CurrentTile =
                        currentFloorId;
                }
            }

            /*
             * =====================================
             * ADOBase
             * =====================================
             */

            Type adoBaseType =
                AccessTools.TypeByName(
                    "ADOBase"
                );

            object conductor = null;
            object levelMaker = null;
            object playerManager = null;

            if (adoBaseType != null)
            {
                conductor =
                    ReadStaticMember(
                        adoBaseType,
                        "conductor"
                    );

                levelMaker =
                    ReadStaticMember(
                        adoBaseType,
                        "lm"
                    );

                playerManager =
                    ReadStaticMember(
                        adoBaseType,
                        "playerManager"
                    );
            }

            /*
             * =====================================
             * ACCURACY / XACCURACY
             * =====================================
             *
             * ADOBase.playerManager
             *     -> mistakesManager
             *         -> percentAcc
             *         -> percentXAcc
             *
             * ゲーム側で計算済みの内部値を
             * そのまま利用する。
             */

            if (playerManager != null)
            {
                object mistakesManager =
                    ReadMember(
                        playerManager,
                        "mistakesManager"
                    );

                if (mistakesManager != null)
                {
                    object accuracyValue =
                        ReadMember(
                            mistakesManager,
                            "percentAcc"
                        );

                    if (
                        TryGetDouble(
                            accuracyValue,
                            out double accuracy
                        )
                    )
                    {
                        data.Accuracy =
                            NormalizeAccuracy(
                                accuracy
                            );
                    }

                    object xAccuracyValue =
                        ReadMember(
                            mistakesManager,
                            "percentXAcc"
                        );

                    if (
                        TryGetDouble(
                            xAccuracyValue,
                            out double xAccuracy
                        )
                    )
                    {
                        data.XAccuracy =
                            NormalizeAccuracy(
                                xAccuracy
                            );
                    }
                }
            }

            /*
             * =====================================
             * BPM / AUDIO
             * =====================================
             */

            if (conductor != null)
            {
                object bpmValue =
                    ReadMember(
                        conductor,
                        "bpm"
                    );

                if (
                    TryGetDouble(
                        bpmValue,
                        out double bpm
                    )
                )
                {
                    data.BaseBpm =
                        bpm;
                }

                object song =
                    ReadMember(
                        conductor,
                        "song"
                    );

                if (song != null)
                {
                    object pitchValue =
                        ReadMember(
                            song,
                            "pitch"
                        );

                    if (
                        TryGetDouble(
                            pitchValue,
                            out double pitch
                        )
                    )
                    {
                        data.SongPitch =
                            pitch;
                    }

                    object timeValue =
                        ReadMember(
                            song,
                            "time"
                        );

                    if (
                        TryGetDouble(
                            timeValue,
                            out double currentTime
                        )
                    )
                    {
                        data.CurrentTime =
                            currentTime;
                    }

                    object clip =
                        ReadMember(
                            song,
                            "clip"
                        );

                    if (clip != null)
                    {
                        object lengthValue =
                            ReadMember(
                                clip,
                                "length"
                            );

                        if (
                            TryGetDouble(
                                lengthValue,
                                out double musicDuration
                            )
                        )
                        {
                            data.MusicDuration =
                                musicDuration;
                        }
                    }
                }

                object addOffsetValue =
                    ReadMember(
                        conductor,
                        "addoffset"
                    );

                object songPositionValue =
                    ReadMember(
                        conductor,
                        "songposition_minusi"
                    );

                if (
                    TryGetDouble(
                        addOffsetValue,
                        out double addOffset
                    ) &&
                    TryGetDouble(
                        songPositionValue,
                        out double songPosition
                    )
                )
                {
                    data.MapTime =
                        addOffset +
                        songPosition;
                }
            }

            /*
             * =====================================
             * CURRENT FLOOR / CURRENT BPM
             * =====================================
             */

            object currentFloor =
                ReadMember(
                    controller,
                    "currFloor"
                );

            if (currentFloor != null)
            {
                object nextFloor =
                    ReadMember(
                        currentFloor,
                        "nextfloor"
                    );

                if (nextFloor != null)
                {
                    object currentEntryValue =
                        ReadMember(
                            currentFloor,
                            "entryTime"
                        );

                    object nextEntryValue =
                        ReadMember(
                            nextFloor,
                            "entryTime"
                        );

                    if (
                        TryGetDouble(
                            currentEntryValue,
                            out double currentEntry
                        ) &&
                        TryGetDouble(
                            nextEntryValue,
                            out double nextEntry
                        )
                    )
                    {
                        double delta =
                            nextEntry -
                            currentEntry;

                        if (
                            delta >
                            0.000001
                        )
                        {
                            double pitch =
                                data.SongPitch
                                ?? 1.0;

                            data.CurrentBpm =
                                (60.0 / delta) *
                                pitch;
                        }
                    }
                }
            }

            /*
             * =====================================
             * TOTAL FLOOR / MAP DURATION
             * =====================================
             */

            if (levelMaker != null)
            {
                object floorsObject =
                    ReadMember(
                        levelMaker,
                        "listFloors"
                    );

                if (
                    floorsObject is IList floors &&
                    floors.Count > 0
                )
                {
                    data.IsValidMap =
                        true;

                    data.TotalTiles =
                        floors.Count;

                    object lastFloor =
                        floors[
                            floors.Count - 1
                        ];

                    if (lastFloor != null)
                    {
                        object entryTimeValue =
                            ReadMember(
                                lastFloor,
                                "entryTime"
                            );

                        if (
                            TryGetDouble(
                                entryTimeValue,
                                out double mapDuration
                            )
                        )
                        {
                            data.MapDuration =
                                mapDuration;
                        }
                    }
                }
            }

            /*
             * =====================================
             * LEVEL DATA
             * =====================================
             */

            Type scnGameType =
                AccessTools.TypeByName(
                    "scnGame"
                );

            if (scnGameType != null)
            {
                object gameInstance =
                    ReadStaticMember(
                        scnGameType,
                        "instance"
                    );

                if (gameInstance != null)
                {
                    object levelData =
                        ReadMember(
                            gameInstance,
                            "levelData"
                        );

                    if (levelData != null)
                    {
                        data.HasLevelData =
                            true;

                        data.Song =
                            ReadStringMember(
                                levelData,
                                "song"
                            );

                        data.Artist =
                            ReadStringMember(
                                levelData,
                                "artist"
                            );

                        data.Charter =
                            ReadStringMember(
                                levelData,
                                "author"
                            );

                        object difficultyValue =
                            ReadMember(
                                levelData,
                                "difficulty"
                            );

                        if (
                            TryGetDouble(
                                difficultyValue,
                                out double difficulty
                            )
                        )
                        {
                            data.Difficulty =
                                difficulty;
                        }
                    }
                }
            }

            /*
             * listFloorsとlevelDataの
             * 両方が存在している状態を
             * 有効な譜面状態とする。
             */
            data.IsValidMap =
                data.IsValidMap &&
                data.HasLevelData;

            return data;
        }

        /*
         * ゲーム内部ではaccuracyが
         * 0～1系の値として保持される。
         *
         * JSON側では%表記向けに
         * 100倍した値を出す。
         *
         * 通常Accuracyは100%を超えることが
         * あるためClampしない。
         */
        private static double NormalizeAccuracy(
            double value
        )
        {
            if (
                value >= 0 &&
                value <= 2.0
            )
            {
                return
                    value *
                    100.0;
            }

            return value;
        }

        private static string CreateMapKey(
            TelemetryData data
        )
        {
            return
                (data.Artist ?? "") +
                "\n" +
                (data.Song ?? "") +
                "\n" +
                (data.Charter ?? "");
        }

        private static void MergePreviousValues(
            TelemetryData current,
            TelemetryData previous
        )
        {
            if (
                string.IsNullOrWhiteSpace(
                    current.Title
                )
            )
            {
                current.Title =
                    previous.Title;
            }

            if (
                string.IsNullOrWhiteSpace(
                    current.Song
                )
            )
            {
                current.Song =
                    previous.Song;
            }

            if (
                string.IsNullOrWhiteSpace(
                    current.Artist
                )
            )
            {
                current.Artist =
                    previous.Artist;
            }

            if (
                string.IsNullOrWhiteSpace(
                    current.Charter
                )
            )
            {
                current.Charter =
                    previous.Charter;
            }

            if (!current.Difficulty.HasValue)
            {
                current.Difficulty =
                    previous.Difficulty;
            }

            if (!current.Progress.HasValue)
            {
                current.Progress =
                    previous.Progress;
            }

            if (!current.CurrentTile.HasValue)
            {
                current.CurrentTile =
                    previous.CurrentTile;
            }

            if (!current.TotalTiles.HasValue)
            {
                current.TotalTiles =
                    previous.TotalTiles;
            }

            if (!current.BaseBpm.HasValue)
            {
                current.BaseBpm =
                    previous.BaseBpm;
            }

            if (!current.CurrentBpm.HasValue)
            {
                current.CurrentBpm =
                    previous.CurrentBpm;
            }

            if (!current.SongPitch.HasValue)
            {
                current.SongPitch =
                    previous.SongPitch;
            }

            if (!current.Accuracy.HasValue)
            {
                current.Accuracy =
                    previous.Accuracy;
            }

            if (!current.XAccuracy.HasValue)
            {
                current.XAccuracy =
                    previous.XAccuracy;
            }

            if (!current.CurrentTime.HasValue)
            {
                current.CurrentTime =
                    previous.CurrentTime;
            }

            if (!current.MapTime.HasValue)
            {
                current.MapTime =
                    previous.MapTime;
            }

            if (!current.MapDuration.HasValue)
            {
                current.MapDuration =
                    previous.MapDuration;
            }

            if (!current.MusicDuration.HasValue)
            {
                current.MusicDuration =
                    previous.MusicDuration;
            }
        }

        private static object ReadMember(
            object instance,
            string name
        )
        {
            if (instance == null)
                return null;

            Type type =
                instance.GetType();

            while (type != null)
            {
                try
                {
                    FieldInfo field =
                        type.GetField(
                            name,
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic
                        );

                    if (field != null)
                    {
                        return
                            field.GetValue(
                                instance
                            );
                    }

                    PropertyInfo property =
                        type.GetProperty(
                            name,
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic
                        );

                    if (
                        property != null &&
                        property
                            .GetIndexParameters()
                            .Length == 0
                    )
                    {
                        return
                            property.GetValue(
                                instance,
                                null
                            );
                    }
                }
                catch
                {
                    return null;
                }

                type =
                    type.BaseType;
            }

            return null;
        }

        private static object ReadStaticMember(
            Type type,
            string name
        )
        {
            if (type == null)
                return null;

            Type current =
                type;

            while (current != null)
            {
                try
                {
                    FieldInfo field =
                        current.GetField(
                            name,
                            BindingFlags.Static |
                            BindingFlags.Public |
                            BindingFlags.NonPublic
                        );

                    if (field != null)
                    {
                        return
                            field.GetValue(
                                null
                            );
                    }

                    PropertyInfo property =
                        current.GetProperty(
                            name,
                            BindingFlags.Static |
                            BindingFlags.Public |
                            BindingFlags.NonPublic
                        );

                    if (
                        property != null &&
                        property
                            .GetIndexParameters()
                            .Length == 0
                    )
                    {
                        return
                            property.GetValue(
                                null,
                                null
                            );
                    }
                }
                catch
                {
                    return null;
                }

                current =
                    current.BaseType;
            }

            return null;
        }

        private static string ReadStringMember(
            object instance,
            string name
        )
        {
            object value =
                ReadMember(
                    instance,
                    name
                );

            return
                value?.ToString()
                ?? "";
        }

        private static string ReadTextMember(
            object instance,
            string name
        )
        {
            object value =
                ReadMember(
                    instance,
                    name
                );

            if (value == null)
                return "";

            if (value is Text text)
            {
                return
                    text.text
                    ?? "";
            }

            object textValue =
                ReadMember(
                    value,
                    "text"
                );

            return
                textValue?.ToString()
                ?? "";
        }

        private static bool TryGetDouble(
            object value,
            out double result
        )
        {
            result = 0;

            if (value == null)
                return false;

            try
            {
                result =
                    Convert.ToDouble(
                        value,
                        CultureInfo.InvariantCulture
                    );

                return
                    !double.IsNaN(
                        result
                    ) &&
                    !double.IsInfinity(
                        result
                    );
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetInt(
            object value,
            out int result
        )
        {
            result = 0;

            if (value == null)
                return false;

            try
            {
                result =
                    Convert.ToInt32(
                        value,
                        CultureInfo.InvariantCulture
                    );

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static double? ParsePercent(
            string text
        )
        {
            if (
                string.IsNullOrWhiteSpace(
                    text
                )
            )
            {
                return null;
            }

            Match match =
                PercentRegex.Match(
                    text
                );

            if (!match.Success)
                return null;

            if (
                double.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double result
                )
            )
            {
                return result;
            }

            return null;
        }

        private static string CreateJson(
            TelemetryData data
        )
        {
            StringBuilder sb =
                new StringBuilder();

            sb.AppendLine("{");

            AppendString(
                sb,
                "title",
                data.Title,
                true
            );

            AppendString(
                sb,
                "song",
                data.Song,
                true
            );

            AppendString(
                sb,
                "artist",
                data.Artist,
                true
            );

            AppendString(
                sb,
                "charter",
                data.Charter,
                true
            );

            AppendNumber(
                sb,
                "difficulty",
                data.Difficulty,
                true
            );

            AppendNumber(
                sb,
                "progress",
                data.Progress,
                true
            );

            AppendString(
                sb,
                "progressText",
                data.ProgressText,
                true
            );

            /*
             * NEW:
             * ゲーム内部のAccuracy
             */
            AppendNumber(
                sb,
                "accuracy",
                data.Accuracy,
                true
            );

            /*
             * NEW:
             * XPerfect基準のAccuracy
             */
            AppendNumber(
                sb,
                "xAccuracy",
                data.XAccuracy,
                true
            );

            AppendNumber(
                sb,
                "currentTile",
                data.CurrentTile,
                true
            );

            AppendNumber(
                sb,
                "totalTiles",
                data.TotalTiles,
                true
            );

            AppendNumber(
                sb,
                "baseBpm",
                data.BaseBpm,
                true
            );

            AppendNumber(
                sb,
                "currentBpm",
                data.CurrentBpm,
                true
            );

            AppendNumber(
                sb,
                "songPitch",
                data.SongPitch,
                true
            );

            AppendNumber(
                sb,
                "currentTime",
                data.CurrentTime,
                true
            );

            AppendNumber(
                sb,
                "mapTime",
                data.MapTime,
                true
            );

            AppendNumber(
                sb,
                "mapDuration",
                data.MapDuration,
                true
            );

            AppendNumber(
                sb,
                "musicDuration",
                data.MusicDuration,
                false
            );

            sb.Append("}");

            return
                sb.ToString();
        }

        private static void AppendString(
            StringBuilder sb,
            string name,
            string value,
            bool comma
        )
        {
            sb.Append("  \"");
            sb.Append(name);
            sb.Append("\":\"");
            sb.Append(
                JsonString(
                    value ?? ""
                )
            );
            sb.Append("\"");

            if (comma)
            {
                sb.Append(",");
            }

            sb.AppendLine();
        }

        private static void AppendNumber(
            StringBuilder sb,
            string name,
            double? value,
            bool comma
        )
        {
            sb.Append("  \"");
            sb.Append(name);
            sb.Append("\":");

            if (value.HasValue)
            {
                sb.Append(
                    value.Value.ToString(
                        "0.########",
                        CultureInfo.InvariantCulture
                    )
                );
            }
            else
            {
                sb.Append(
                    "null"
                );
            }

            if (comma)
            {
                sb.Append(",");
            }

            sb.AppendLine();
        }

        private static string JsonString(
            string value
        )
        {
            if (value == null)
                return "";

            StringBuilder sb =
                new StringBuilder();

            foreach (
                char c
                in value
            )
            {
                switch (c)
                {
                    case '\\':
                        sb.Append(
                            "\\\\"
                        );
                        break;

                    case '"':
                        sb.Append(
                            "\\\""
                        );
                        break;

                    case '\r':
                        sb.Append(
                            "\\r"
                        );
                        break;

                    case '\n':
                        sb.Append(
                            "\\n"
                        );
                        break;

                    case '\t':
                        sb.Append(
                            "\\t"
                        );
                        break;

                    default:

                        if (c < 32)
                        {
                            sb.Append(
                                "\\u" +
                                ((int)c)
                                    .ToString(
                                        "x4"
                                    )
                            );
                        }
                        else
                        {
                            sb.Append(
                                c
                            );
                        }

                        break;
                }
            }

            return
                sb.ToString();
        }

        private sealed class TelemetryData
        {
            public bool IsValidMap;
            public bool HasLevelData;

            public string Title = "";
            public string Song = "";
            public string Artist = "";
            public string Charter = "";
            public string ProgressText = "";

            public double? Difficulty;

            public double? Progress;

            /*
             * NEW
             */
            public double? Accuracy;
            public double? XAccuracy;

            public double? CurrentTile;
            public double? TotalTiles;

            public double? BaseBpm;
            public double? CurrentBpm;
            public double? SongPitch;

            public double? CurrentTime;
            public double? MapTime;
            public double? MapDuration;
            public double? MusicDuration;

            public TelemetryData Clone()
            {
                return
                    new TelemetryData
                    {
                        IsValidMap =
                            IsValidMap,

                        HasLevelData =
                            HasLevelData,

                        Title =
                            Title,

                        Song =
                            Song,

                        Artist =
                            Artist,

                        Charter =
                            Charter,

                        ProgressText =
                            ProgressText,

                        Difficulty =
                            Difficulty,

                        Progress =
                            Progress,

                        Accuracy =
                            Accuracy,

                        XAccuracy =
                            XAccuracy,

                        CurrentTile =
                            CurrentTile,

                        TotalTiles =
                            TotalTiles,

                        BaseBpm =
                            BaseBpm,

                        CurrentBpm =
                            CurrentBpm,

                        SongPitch =
                            SongPitch,

                        CurrentTime =
                            CurrentTime,

                        MapTime =
                            MapTime,

                        MapDuration =
                            MapDuration,

                        MusicDuration =
                            MusicDuration
                    };
            }
        }
    }
}