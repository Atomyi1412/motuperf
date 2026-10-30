using System;
using System.Threading.Tasks;

namespace MoTuPerf.Desktop
{
    internal static class SessionSaveDecision
    {
        internal static async Task<bool> CanContinueAsync(bool hasData, Func<Task<bool?>> chooseSave, Func<Task<bool>> save)
        {
            if (!hasData) return true;
            bool? choice = await chooseSave();
            if (!choice.HasValue) return false;
            return !choice.Value || await save();
        }
    }
}
