using System;

namespace SymbolPrint.Core.Util
{
    /// <summary>进度 + 取消（与 VB 版语义一致）。fraction &lt; 0 表示不确定进度。</summary>
    public interface IProgressSink
    {
        void Report(string stage, double fraction, string detail);
        bool IsCancelled { get; }
    }

    public sealed class NullProgress : IProgressSink
    {
        public static readonly NullProgress Instance = new NullProgress();
        private NullProgress() { }
        public bool IsCancelled => false;
        public void Report(string stage, double fraction, string detail) { }
    }

    public static class ProgressExt
    {
        public static void Push(IProgressSink sink, string stage, double fraction, string detail = null)
        {
            sink?.Report(stage, fraction, detail);
        }

        public static void ThrowIfCancelled(this IProgressSink sink)
        {
            if (sink != null && sink.IsCancelled) throw new OperationCanceledException("操作已被用户取消。");
        }

        public static string HumanSize(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024.0).ToString("0.0") + " KB";
            return (n / 1024.0 / 1024.0).ToString("0.00") + " MB";
        }

        public static string BytesDetail(long done, long total)
        {
            if (total <= 0) return HumanSize(done);
            return HumanSize(done) + " / " + HumanSize(total);
        }
    }
}
