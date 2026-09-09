using System.Text;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;

var tests = new (string Name, Func<Task> Run)[]
{
    ("중첩 AND/OR와 제외 검색", TestNestedSearch),
    ("검색 옵션과 문맥", TestSearchOptionsAndContext),
    ("머리/꼬리 자르기", TestHeadAndTailTrim),
    ("사이 지우기", TestRemoveBetween),
    ("치환 및 공백 정리", TestReplaceAndWhitespace),
    ("중복/빈 줄 정리", TestLineCleanup),
    ("특정 문장 포함 줄 삭제", TestRemoveLinesContaining),
    ("포함 줄 삭제 경계와 줄바꿈 보존", TestRemoveLinesContainingBoundaries),
    ("추출 및 삭제", TestExtractAndDelete),
    ("UTF-8/UTF-16/CP949 파일 보존", TestFileEncoding),
    ("표현 불가능 문자 저장 차단", TestEncodingLossPrevention),
    ("안전한 파일 교체와 임시 파일 정리", TestSafeFileReplacement),
    ("사이 내용만 남기기", TestKeepBetween),
    ("Unicode 글자 수 자르기", TestUnicodeCharacterRemoval),
    ("접두/접미 추가", TestPrefixAndSuffix),
    ("구분자 분리와 줄 합치기", TestSplitAndJoin),
    ("줄번호 추가와 제거", TestLineNumbers),
    ("UTF-8 직접 로드 버퍼", TestUtf8LoadBuffer),
    ("UTF-16/CP949 UTF-8 변환 버퍼", TestTranscodedLoadBuffer),
    ("잘못된 UTF-8 버퍼 차단", TestInvalidUtf8Buffer),
    ("범위 기반 검색과 기존 API 회귀", TestRangeSearch)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed");
return failed == 0 ? 0 : 1;

static Task TestNestedSearch()
{
    var condition = new ConditionGroup(ConditionOperator.All,
    [
        new ConditionGroup(ConditionOperator.Any,
        [
            new TextCondition(TextConditionKind.Contains, "AAA"),
            new TextCondition(TextConditionKind.Contains, "BBB")
        ]),
        new TextCondition(TextConditionKind.DoesNotContain, "CC")
    ]);
    var results = new TextSearchEngine().Search("AAA good\r\nBBB CC\r\nnone", condition);
    Assert.Equal(1, results.Count);
    Assert.Equal(1, results[0].LineNumber);
    return Task.CompletedTask;
}

static Task TestSearchOptionsAndContext()
{
    var condition = new TextCondition(TextConditionKind.Contains, "cat");
    var engine = new TextSearchEngine();
    Assert.Equal(2, engine.Search("before\ncat\nconcatenate\nafter", condition).Count);
    var result = engine.Search("before\ncat\nconcatenate\nafter", condition,
        new SearchOptions(MatchCase: true, WholeWord: true, ContextLines: 1));
    Assert.Equal(1, result.Count);
    Assert.SequenceEqual(new[] { 1, 2, 3 }, result[0].Context.Select(item => item.LineNumber));
    Assert.Equal(0, engine.Search("AAA", new TextCondition(TextConditionKind.Contains, " ")).Count);
    return Task.CompletedTask;
}

static Task TestHeadAndTailTrim()
{
    var service = new TextTransformService();
    var head = service.Trim("앞AAA내용\r\n기준없음", new TrimOptions(TrimOperation.RemoveBefore, "AAA"));
    Assert.Equal("AAA내용\r\n기준없음", head.Text);
    Assert.Equal(1, head.Summary.ChangedLines);
    Assert.Equal(1, head.Summary.SkippedLines);
    var tail = service.Trim("내용BBB뒤", new TrimOptions(TrimOperation.RemoveAfter, "BBB", KeepStartMarker: false));
    Assert.Equal("내용", tail.Text);
    return Task.CompletedTask;
}

static Task TestRemoveBetween()
{
    var service = new TextTransformService();
    var kept = service.Trim("앞AAA삭제BBB뒤", new TrimOptions(TrimOperation.RemoveBetween, "AAA", "BBB"));
    Assert.Equal("앞AAABBB뒤", kept.Text);
    var removed = service.Trim("앞AAA삭제BBB뒤",
        new TrimOptions(TrimOperation.RemoveBetween, "AAA", "BBB", false, false));
    Assert.Equal("앞뒤", removed.Text);
    var missing = service.Trim("BBB앞AAA", new TrimOptions(TrimOperation.RemoveBetween, "AAA", "BBB"));
    Assert.Equal(TextChangeStatus.Skipped, missing.Preview[0].Status);
    return Task.CompletedTask;
}

static Task TestReplaceAndWhitespace()
{
    var service = new TextTransformService();
    Assert.Equal("xx", service.Replace("AaAA", "aa", "x").Text);
    Assert.Equal("한글\r\ntext", service.TrimWhitespace("  한글 \r\n\ttext\t").Text);
    return Task.CompletedTask;
}

static Task TestLineCleanup()
{
    var service = new TextTransformService();
    Assert.Equal("A\r\nB", service.RemoveDuplicateLines("A\r\na\r\nB").Text);
    Assert.Equal("A\r\nB", service.RemoveBlankLines("A\r\n  \r\nB").Text);
    Assert.Equal("A\r\n\r\nB", service.CollapseBlankLines("A\r\n\r\n \r\nB").Text);
    return Task.CompletedTask;
}

static Task TestRemoveLinesContaining()
{
    var service = new TextTransformService();
    var text = "유지할 한글\r\n오류: 연결 실패\r\nERROR: timeout\r\nerror: retry\r\n\r\n마지막 줄";

    var korean = service.RemoveLinesContaining(text, "연결 실패");
    Assert.Equal("유지할 한글\r\nERROR: timeout\r\nerror: retry\r\n\r\n마지막 줄", korean.Text);
    Assert.Equal(1, korean.Summary.ChangedLines);
    Assert.Equal(TextChangeStatus.Changed, korean.Preview[1].Status);
    Assert.Equal(string.Empty, korean.Preview[1].ResultText);

    var ignoreCase = service.RemoveLinesContaining("ERROR: timeout\nerror: retry\n정상", "error", newLine: "\n");
    Assert.Equal("정상", ignoreCase.Text);
    Assert.Equal(2, ignoreCase.Summary.ChangedLines);

    var matchCase = service.RemoveLinesContaining("ERROR: timeout\nerror: retry\n정상", "error", true, "\n");
    Assert.Equal("ERROR: timeout\n정상", matchCase.Text);

    var blankLine = service.RemoveLinesContaining("첫째\r\n\r\n셋째", "찾을 문장");
    Assert.Equal("첫째\r\n\r\n셋째", blankLine.Text);
    Assert.Equal(0, blankLine.Summary.ChangedLines);
    Assert.Equal(TextChangeStatus.Unchanged, blankLine.Preview[1].Status);

    var trailingNewLine = service.RemoveLinesContaining("삭제 문장\n유지\n", "삭제", newLine: "\n");
    Assert.Equal("유지\n", trailingNewLine.Text);
    Assert.Equal(2, trailingNewLine.Preview.Count);

    Assert.Throws<ArgumentException>(() => service.RemoveLinesContaining("내용", string.Empty));
    return Task.CompletedTask;
}

static Task TestRemoveLinesContainingBoundaries()
{
    var service = new TextTransformService();
    foreach (var newLine in new[] { "\r\n", "\n", "\r" })
    {
        Assert.Equal($"둘{newLine}셋",
            service.RemoveLinesContaining($"삭제{newLine}둘{newLine}셋", "삭제", newLine: newLine).Text);
        Assert.Equal($"첫{newLine}셋",
            service.RemoveLinesContaining($"첫{newLine}삭제{newLine}셋", "삭제", newLine: newLine).Text);
        Assert.Equal($"첫{newLine}둘",
            service.RemoveLinesContaining($"첫{newLine}둘{newLine}삭제", "삭제", newLine: newLine).Text);

        Assert.Equal($"둘{newLine}셋{newLine}",
            service.RemoveLinesContaining($"삭제{newLine}둘{newLine}셋{newLine}", "삭제", newLine: newLine).Text);
        Assert.Equal($"첫{newLine}셋{newLine}",
            service.RemoveLinesContaining($"첫{newLine}삭제{newLine}셋{newLine}", "삭제", newLine: newLine).Text);
        Assert.Equal($"첫{newLine}둘{newLine}",
            service.RemoveLinesContaining($"첫{newLine}둘{newLine}삭제{newLine}", "삭제", newLine: newLine).Text);

        Assert.Equal(string.Empty,
            service.RemoveLinesContaining("삭제", "삭제", newLine: newLine).Text);
        Assert.Equal(string.Empty,
            service.RemoveLinesContaining($"삭제{newLine}", "삭제", newLine: newLine).Text);
        Assert.Equal(string.Empty,
            service.RemoveLinesContaining($"삭제1{newLine}삭제2", "삭제", newLine: newLine).Text);
        Assert.Equal(string.Empty,
            service.RemoveLinesContaining($"삭제1{newLine}삭제2{newLine}", "삭제", newLine: newLine).Text);

        var onlyLine = service.RemoveLinesContaining($"삭제{newLine}", "삭제", newLine: newLine);
        Assert.Equal(1, onlyLine.Preview.Count);
        Assert.Equal(1, onlyLine.Summary.ChangedLines);

        var unchanged = $"첫{newLine}둘{newLine}";
        Assert.Equal(unchanged,
            service.RemoveLinesContaining(unchanged, "없음", newLine: newLine).Text);
    }

    return Task.CompletedTask;
}

static Task TestExtractAndDelete()
{
    var service = new TextTransformService();
    var text = "one\r\ntwo\r\nthree\r\nfour";
    Assert.Equal("one\r\ntwo\r\nthree", service.ExtractLines(text, [2], 1).Text);
    Assert.Equal("one\r\nthree", service.DeleteLines(text, [2, 4]).Text);
    return Task.CompletedTask;
}

static async Task TestFileEncoding()
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var service = new DocumentFileService();
    var directory = Path.Combine(Path.GetTempPath(), "MyTextEditor.Core.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var cases = new (string Name, Encoding Encoding, bool Bom, string NewLine)[]
        {
            ("utf8.txt", new UTF8Encoding(false, true), false, "\n"),
            ("utf16.txt", new UnicodeEncoding(false, true, true), true, "\r\n"),
            ("cp949.txt", Encoding.GetEncoding(949), false, "\r\n")
        };
        foreach (var item in cases)
        {
            var path = Path.Combine(directory, item.Name);
            var body = item.Encoding.GetBytes($"가나다{item.NewLine}ABC");
            var bytes = item.Bom ? item.Encoding.GetPreamble().Concat(body).ToArray() : body;
            await File.WriteAllBytesAsync(path, bytes);
            var document = await service.LoadAsync(path);
            Assert.Equal(item.NewLine, document.NewLine);
            Assert.Equal("가나다" + item.NewLine + "ABC", document.Text);
            document.Text = "가나다\r\nABC";
            await service.SaveAsync(document);
            Assert.SequenceEqual(bytes, await File.ReadAllBytesAsync(path));
        }
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static async Task TestEncodingLossPrevention()
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var service = new DocumentFileService();
    var document = new DocumentState
    {
        Text = "emoji 😀",
        Encoding = Encoding.GetEncoding(949),
        HasByteOrderMark = false
    };
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
    await Assert.ThrowsAsync<DocumentEncodingException>(() => service.SaveAsync(document, path));
    Assert.False(File.Exists(path));
}

static async Task TestSafeFileReplacement()
{
    var service = new DocumentFileService();
    var directory = Path.Combine(Path.GetTempPath(), "MyTextEditor.Core.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "document.txt");
        await File.WriteAllTextAsync(path, "기존 내용", new UTF8Encoding(false));
        var document = new DocumentState
        {
            FilePath = path,
            Text = "새 내용\r\n두 번째 줄",
            Encoding = new UTF8Encoding(false, true),
            HasByteOrderMark = false,
            NewLine = "\n",
            IsModified = true
        };

        await service.SaveAsync(document);

        Assert.Equal("새 내용\n두 번째 줄", await File.ReadAllTextAsync(path, new UTF8Encoding(false, true)));
        Assert.False(document.IsModified);
        Assert.Equal(0, Directory.GetFiles(directory, ".document.txt.*.tmp").Length);

        document.Text = "저장되면 안 됨 😀";
        document.Encoding = Encoding.GetEncoding(949);
        document.IsModified = true;
        await Assert.ThrowsAsync<DocumentEncodingException>(() => service.SaveAsync(document));
        Assert.Equal("새 내용\n두 번째 줄", await File.ReadAllTextAsync(path, new UTF8Encoding(false, true)));
        Assert.True(document.IsModified);
        Assert.Equal(0, Directory.GetFiles(directory, ".document.txt.*.tmp").Length);

        var newPath = Path.Combine(directory, "new-document.txt");
        var newDocument = new DocumentState { Text = "새 파일", IsModified = true };
        await service.SaveAsync(newDocument, newPath);
        Assert.Equal("새 파일", await File.ReadAllTextAsync(newPath, new UTF8Encoding(false, true)));
        Assert.Equal(Path.GetFullPath(newPath), newDocument.FilePath);
        Assert.False(newDocument.IsModified);
        Assert.Equal(0, Directory.GetFiles(directory, ".new-document.txt.*.tmp").Length);
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static Task TestKeepBetween()
{
    var service = new TextTransformService();
    var keepMarkers = service.Trim("앞[시작]내용[끝]뒤[끝]",
        new TrimOptions(TrimOperation.KeepBetween, "[시작]", "[끝]"));
    Assert.Equal("[시작]내용[끝]", keepMarkers.Text);

    var removeMarkers = service.Trim("앞AAA내용BBB뒤",
        new TrimOptions(TrimOperation.KeepBetween, "AAA", "BBB", false, false));
    Assert.Equal("내용", removeMarkers.Text);

    var keepStartOnly = service.Trim("앞AAA내용BBB뒤",
        new TrimOptions(TrimOperation.KeepBetween, "AAA", "BBB", true, false));
    Assert.Equal("AAA내용", keepStartOnly.Text);

    var missing = service.Trim("앞AAA내용", new TrimOptions(TrimOperation.KeepBetween, "AAA", "BBB"));
    Assert.Equal("앞AAA내용", missing.Text);
    Assert.Equal(TextChangeStatus.Skipped, missing.Preview[0].Status);
    return Task.CompletedTask;
}

static Task TestUnicodeCharacterRemoval()
{
    var service = new TextTransformService();
    const string family = "👨‍👩‍👧‍👦";
    const string combined = "e\u0301";
    var text = "한" + family + combined + "글";
    Assert.Equal(combined + "글",
        service.RemoveCharacters(text, 2, CharacterRemovalSide.Left).Text);
    Assert.Equal("한" + family,
        service.RemoveCharacters(text, 2, CharacterRemovalSide.Right).Text);
    Assert.Equal(string.Empty,
        service.RemoveCharacters("가😀", 5, CharacterRemovalSide.Left).Text);
    Assert.Equal(text,
        service.RemoveCharacters(text, 0, CharacterRemovalSide.Right).Text);
    return Task.CompletedTask;
}

static Task TestPrefixAndSuffix()
{
    var service = new TextTransformService();
    var text = "첫째\r\n\r\n  \r\n셋째";
    Assert.Equal("> 첫째\r\n\r\n  \r\n> 셋째", service.AddPrefix(text, "> ").Text);
    Assert.Equal("첫째!\r\n!\r\n  !\r\n셋째!", service.AddSuffix(text, "!", true).Text);
    Assert.Equal(2, service.AddPrefix(text, "> ").Summary.SkippedLines);
    return Task.CompletedTask;
}

static Task TestSplitAndJoin()
{
    var service = new TextTransformService();
    var split = service.SplitByDelimiter(" A |||| B ", "||", "\n");
    Assert.Equal(" A \n\n B ", split.Text);
    Assert.Equal(1, split.Summary.ChangedLines);

    var joined = service.JoinLines(" A \n\n B ", "||");
    Assert.Equal(" A |||| B ", joined.Text);
    Assert.Equal("abc", service.SplitByDelimiter("abc", ",").Text);
    return Task.CompletedTask;
}

static Task TestLineNumbers()
{
    var service = new TextTransformService();
    var text = "alpha\r\n\r\n beta ";
    var numbered = service.AddLineNumbers(text, 7, " | ");
    Assert.Equal("7 | alpha\r\n\r\n8 |  beta ", numbered.Text);
    Assert.Equal(text, service.RemoveLineNumbers(numbered.Text, " | ").Text);

    var includingBlank = service.AddLineNumbers(text, -1, ")", true);
    Assert.Equal("-1)alpha\r\n0)\r\n1) beta ", includingBlank.Text);
    Assert.Equal(text, service.RemoveLineNumbers(includingBlank.Text, ")").Text);

    var overlappingSeparator = service.AddLineNumbers("alpha", -1, "-");
    Assert.Equal("alpha", service.RemoveLineNumbers(overlappingSeparator.Text, "-").Text);

    var foreign = service.RemoveLineNumbers("번호)text\r\n12-other", ")");
    Assert.Equal("번호)text\r\n12-other", foreign.Text);
    Assert.Equal(2, foreign.Summary.SkippedLines);
    return Task.CompletedTask;
}

static async Task TestUtf8LoadBuffer()
{
    var service = new DocumentFileService();
    var directory = Path.Combine(Path.GetTempPath(), "MyTextEditor.Core.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        const string text = "한글😀\0값\n둘째";
        var body = new UTF8Encoding(false, true).GetBytes(text);
        var withoutBomPath = Path.Combine(directory, "utf8.txt");
        await File.WriteAllBytesAsync(withoutBomPath, body);
        var withoutBom = await service.LoadBufferAsync(withoutBomPath);
        Assert.SequenceEqual(body, withoutBom.Utf8Buffer.ToArray());
        Assert.False(withoutBom.HasByteOrderMark);
        Assert.Equal(65001, withoutBom.OriginalEncoding.CodePage);
        Assert.Equal("\n", withoutBom.NewLine);
        Assert.Equal(Path.GetFullPath(withoutBomPath), withoutBom.FilePath);

        var withBomPath = Path.Combine(directory, "utf8-bom.txt");
        await File.WriteAllBytesAsync(withBomPath, Encoding.UTF8.GetPreamble().Concat(body).ToArray());
        var withBom = await service.LoadBufferAsync(withBomPath);
        Assert.SequenceEqual(body, withBom.Utf8Buffer.ToArray());
        Assert.True(withBom.HasByteOrderMark);
        Assert.Equal(text, new UTF8Encoding(false, true).GetString(withBom.Utf8Buffer.Span));
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static async Task TestTranscodedLoadBuffer()
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var service = new DocumentFileService();
    var directory = Path.Combine(Path.GetTempPath(), "MyTextEditor.Core.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        const string unicodeText = "한글😀\0값\r\n둘째";
        var utf16 = new UnicodeEncoding(false, true, true);
        var utf16Path = Path.Combine(directory, "utf16.txt");
        await File.WriteAllBytesAsync(utf16Path,
            utf16.GetPreamble().Concat(utf16.GetBytes(unicodeText)).ToArray());
        var utf16Buffer = await service.LoadBufferAsync(utf16Path);
        Assert.Equal(unicodeText, new UTF8Encoding(false, true).GetString(utf16Buffer.Utf8Buffer.Span));
        Assert.Equal(1200, utf16Buffer.OriginalEncoding.CodePage);
        Assert.True(utf16Buffer.HasByteOrderMark);
        Assert.Equal("\r\n", utf16Buffer.NewLine);

        const string koreanText = "가나다\0ABC\r끝";
        var cp949 = Encoding.GetEncoding(949,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var cp949Path = Path.Combine(directory, "cp949.txt");
        await File.WriteAllBytesAsync(cp949Path, cp949.GetBytes(koreanText));
        var cp949Buffer = await service.LoadBufferAsync(cp949Path);
        Assert.Equal(koreanText, new UTF8Encoding(false, true).GetString(cp949Buffer.Utf8Buffer.Span));
        Assert.Equal(949, cp949Buffer.OriginalEncoding.CodePage);
        Assert.False(cp949Buffer.HasByteOrderMark);
        Assert.Equal("\r", cp949Buffer.NewLine);
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static async Task TestInvalidUtf8Buffer()
{
    var service = new DocumentFileService();
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
    await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, 0xC3, 0x28]);
    try
    {
        await Assert.ThrowsAsync<DecoderFallbackException>(() => service.LoadBufferAsync(path));
        await Assert.ThrowsAsync<DecoderFallbackException>(() => service.LoadAsync(path));
    }
    finally
    {
        File.Delete(path);
    }
}

static Task TestRangeSearch()
{
    const string text = "첫줄\r\n한글 AAA😀\nNUL\0AAA\r마지막";
    var condition = new TextCondition(TextConditionKind.Contains, "AAA");
    var options = new SearchOptions(ContextLines: 1);
    var engine = new TextSearchEngine();
    var ranges = engine.SearchRanges(text, condition, options);
    var materialized = engine.Search(text, condition, options);

    Assert.Equal(2, ranges.Count);
    Assert.Equal("한글 AAA😀", text.Substring(ranges[0].Range.Start, ranges[0].Range.Length));
    Assert.Equal("NUL\0AAA", text.Substring(ranges[1].Range.Start, ranges[1].Range.Length));
    Assert.SequenceEqual(new[] { 1, 2, 3 }, ranges[0].Context.Select(item => item.LineNumber));
    Assert.SequenceEqual(materialized.Select(item => item.LineNumber), ranges.Select(item => item.LineNumber));
    Assert.SequenceEqual(materialized.Select(item => item.Text),
        ranges.Select(item => text.Substring(item.Range.Start, item.Range.Length)));
    Assert.Equal("NUL\0AAA", materialized[1].Text);
    return Task.CompletedTask;
}

static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected <{expected}> but was <{actual}>.");
    }

    public static void False(bool actual)
    {
        if (actual) throw new InvalidOperationException("Expected false but was true.");
    }

    public static void True(bool actual)
    {
        if (!actual) throw new InvalidOperationException("Expected true but was false.");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Sequences differ.");
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
