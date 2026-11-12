using XiangqiOverlay.Engine;

namespace LogicTest;

public static class EngineSearchSchedulerTests
{
    public static void Run(Action<string, bool> check)
    {
        var scheduler = new EngineSearchScheduler();
        var first = scheduler.Submit("fen-a", 30, 0, 4000, out bool startFirst);
        check("首个搜索请求立即成为活动搜索",
            startFirst && scheduler.Active == first && first.Id == 1 && first.Fen == "fen-a");
        check("搜索命令同时包含深度和毫秒时间",
            first.BuildGoCommand() == "go depth 30 movetime 4000");

        scheduler.Submit("fen-b", 30, 0, 4000, out bool startSecond);
        var latest = scheduler.Submit("fen-c", 30, 0, 4000, out bool startThird);
        check("活动搜索期间不直接启动新搜索",
            !startSecond && !startThird && scheduler.Active == first);
        check("连续请求只保留最新待搜索盘面",
            scheduler.Pending == latest && latest.Fen == "fen-c");

        var started = scheduler.CompleteActive();
        check("旧 bestmove 完成后启动最新待处理请求",
            started == latest && scheduler.Active == latest && scheduler.Pending == null);

        scheduler.Submit("fen-d", 30, 0, 4000, out _);
        scheduler.CancelPending();
        check("停止操作取消待处理请求但保留活动会话归属",
            scheduler.Pending == null && scheduler.Active == latest);
        check("没有待处理请求时完成活动搜索后进入空闲",
            scheduler.CompleteActive() == null && scheduler.Active == null);

        scheduler.Submit("fen-e", 30, 0, 4000, out _);
        scheduler.Reset();
        check("引擎重启清空旧会话",
            scheduler.Active == null && scheduler.Pending == null);
    }
}
