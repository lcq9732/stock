namespace StockPlatform.Logic.Services;

/// <summary>
/// 「待手工回填清单」的生成/导入记录（2026-10-21）。跟清单放在一起的
/// <c>待手工回填清单.state.json</c> 里存的就是它；读写在 Data 层（ManualFillStateStore）。
/// </summary>
/// <param name="GeneratedFileTimeUtc">生成时写完文件那一刻的**文件修改时间**——不是"当前时间"，比的时候才没有误差。</param>
/// <param name="ImportedFileTimeUtc">上次导入的是哪一版（导入时那个文件的修改时间）；生成时清空。</param>
public sealed record ManualFillState(DateTime GeneratedFileTimeUtc, DateTime? ImportedFileTimeUtc);

/// <summary>【导入手工数据】这一轮要不要导入。</summary>
public enum ManualImportDecision
{
    /// <summary>清单不存在——没活。</summary>
    NoFile,
    /// <summary>没有生成记录（改动上线前生成的旧清单）——自动跑不导，往保守的方向走。</summary>
    NoRecord,
    /// <summary>清单生成后没被改过——人还没看，自动导入会替他把 OCR 值全标成"已核对"。</summary>
    NotEdited,
    /// <summary>这一版已经导入过。</summary>
    AlreadyImported,
    /// <summary>导入。</summary>
    Import,
}

/// <summary>
/// 【导入手工数据】的判据（2026-10-21，随这一项挪进日更）。**纯计算，不碰文件。**
///
/// ════ 为什么要它 ════
/// 清单有一条约定：OCR 值有数、最后一列留空＝"我看过了，OCR 认得对"，导入时升级成 ocr_confirmed、
/// 以后不再被自动解析覆盖。原来这一项跟【金融监管指标】在季度组里挨着自动跑——监管指标刚生成清单，
/// 紧接着的自动导入就把所有 OCR 值标成"已核对"，人一眼都没看过。挪进日更会让这件事天天发生。
///
/// 所以自动跑只认"人改过、而且这一版还没导过"：清单的修改时间晚于生成时刻、又不等于上次导入的那一版。
/// 人手动点【执行】就是要导，清单在就导（照旧把留空的行当作 OCR 认可）。
/// </summary>
public static class ManualFillImportRule
{
    public static ManualImportDecision Decide(bool fileExists, DateTime fileTimeUtc, ManualFillState? state, bool manual)
    {
        if (!fileExists) return ManualImportDecision.NoFile;
        if (manual) return ManualImportDecision.Import;
        if (state == null) return ManualImportDecision.NoRecord;
        if (fileTimeUtc <= state.GeneratedFileTimeUtc) return ManualImportDecision.NotEdited;
        if (state.ImportedFileTimeUtc == fileTimeUtc) return ManualImportDecision.AlreadyImported;
        return ManualImportDecision.Import;
    }

    /// <summary>
    /// 重新生成清单之前问一句：现有这份是不是人改过、还没导入？是的话**不能覆盖**——
    /// 日更里【金融监管指标】排在导入前面，覆盖了就是把人填的数静默扔掉。
    /// 没有生成记录的旧清单照原来的行为覆盖（判断不了改没改过，而原来一直就是覆盖）。
    /// </summary>
    public static bool HasUnimportedEdits(bool fileExists, DateTime fileTimeUtc, ManualFillState? state)
        => fileExists && state != null
           && fileTimeUtc > state.GeneratedFileTimeUtc
           && state.ImportedFileTimeUtc != fileTimeUtc;
}
