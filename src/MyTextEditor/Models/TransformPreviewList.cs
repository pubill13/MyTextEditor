using System.Collections;
using MyTextEditor.Core.Models;

namespace MyTextEditor.Models;

// WPF can virtualize an IList without eagerly constructing every row's strings.
public sealed class TransformPreviewList : IList
{
    private readonly IReadOnlyList<TextChangePreview> _source;
    private readonly int[]? _indices;
    public TransformPreviewList(IReadOnlyList<TextChangePreview> source, TextChangeStatus? filter)
    {
        _source = source;
        if (filter.HasValue)
        {
            var indices = new List<int>();
            for (var index = 0; index < source.Count; index++)
                if ((source is ITextChangePreviewList lazy ? lazy.GetStatus(index) : source[index].Status) == filter)
                    indices.Add(index);
            _indices = indices.ToArray();
        }
    }
    public int Count => _indices?.Length ?? _source.Count;
    public object? this[int index]
    {
        get
        {
            var row = _source[_indices?[index] ?? index];
            return new TransformPreviewRow(row.LineNumber, row.OriginalText, row.ResultText,
                row.Status switch { TextChangeStatus.Changed => "변경", TextChangeStatus.Skipped => "건너뜀", _ => "유지" });
        }
        set => throw new NotSupportedException();
    }
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int IndexOf(object? value)
    {
        if (value is not TransformPreviewRow row) return -1;
        var sourceIndex = row.LineNumber - 1;
        var index = _indices is null ? sourceIndex : Array.BinarySearch(_indices, sourceIndex);
        return index >= 0 && index < Count ? index : -1;
    }
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator GetEnumerator() { for (var index = 0; index < Count; index++) yield return this[index]; }
    public void CopyTo(Array array, int index) { foreach (var row in this) array.SetValue(row, index++); }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
