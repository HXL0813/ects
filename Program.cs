using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;
using System.Linq;
using Impinj.OctaneSdk;

namespace OctaneSdkExamples
{
    /// <summary>
    /// 公共静态类，用于存储全局状态和工具方法
    /// </summary>
    public static class Common
    {
        public static int UniqueTagCount = 0;          // 记录唯一标签数量
        public static HashSet<string> SeenTags = new HashSet<string>(); // 存储已见标签的集合
        public static Stopwatch Stopwatch = new Stopwatch(); // 高精度计时器
        public static long StartTime;                  // 测试开始时间（计时周期）
        public static long EndTime;                    // 测试结束时间（计时周期）
        public static long LastTagTime;                // 最后收到标签的时间戳
        
        // 将Stopwatch的计时周期转换为微秒的系数
        public static double TicksToMicroseconds = 1000000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// 通信协议分析工具类
    /// </summary>
    static class AnalysisTools
    {
        /// <summary>
        /// 生成随机二进制字符串
        /// </summary>
        /// <param name="count">需要生成的字符串数量</param>
        /// <param name="length">每个字符串的二进制位数</param>
        /// <returns>生成的二进制字符串列表</returns>
        public static List<string> GenerateBinaryStrings(int count, int length)
        {
            Random rand = new Random();
            return Enumerable.Range(0, count)
                .Select(_ => string.Concat(
                    Enumerable.Range(0, length)
                        .Select(__ => rand.Next(0, 2).ToString())
                )).ToList();
        }

        /// <summary>
        /// 检查子串在剩余标签中是否存在
        /// </summary>
        /// <param name="neededSubstring">需要检查的子串</param>
        /// <param name="remainingTags">剩余标签列表</param>
        /// <param name="start">子串起始位置</param>
        /// <param name="end">子串结束位置</param>
        /// <returns>是否存在相同子串</returns>
        public static bool IfExist(string neededSubstring, List<string> remainingTags, int start, int end)
        {
            return remainingTags.Any(tag => 
                tag.Length >= end && 
                tag.Substring(start, end - start) == neededSubstring);
        }

        /// <summary>
        /// 统计连续数字组的数量和间隔总和
        /// </summary>
        /// <param name="N">总数字范围</param>
        /// <param name="n">选择数字数量</param>
        /// <returns>(连续组数量, 间隔总和)</returns>
        public static (int Groups, int Sum) CountConsecutiveGroups(int N, int n)
        {
            var selected = Enumerable.Range(1, N)
                .OrderBy(_ => Guid.NewGuid()) // 随机排序
                .Take(n)
                .OrderBy(x => x)
                .ToList();

            if (selected.Count == 0) return (0, 0);

            List<List<int>> groups = new List<List<int>>();
            List<int> currentGroup = new List<int> { selected[0] };

            // 构建连续分组
            for (int i = 1; i < selected.Count; i++)
            {
                if (selected[i] == selected[i-1] + 1)
                {
                    currentGroup.Add(selected[i]);
                }
                else
                {
                    groups.Add(currentGroup);
                    currentGroup = new List<int> { selected[i] };
                }
            }
            groups.Add(currentGroup);

            // 计算总间隔
            int totalGap = groups.Sum(g => n - g.Count);
            return (groups.Count, totalGap);
        }

        /// <summary>
        /// 执行通信协议分析
        /// </summary>
        /// <param name="neededTags">需要识别的标签列表</param>
        /// <param name="N">总标签数量</param>
        /// <param name="n">需要识别的标签数量</param>
        /// <param name="Limit">算法模式选择</param>
        /// <param name="ECTS">是否启用ECTS算法</param>
        /// <returns>(SELECT命令数, 总比特数)</returns>
        public static (int ECTSSelect, int ECTSBits) AnalyzeTags(
            List<string> neededTags, int N, int n, int Limit = 0, int ECTS = 1)
        {
            const int BIT_LENGTH = 32;  // 标签二进制长度
            int L_min, L_max;          // 子串长度范围

            // 根据算法模式设置子串长度范围
            switch (Limit)
            {
                case 0:
                    L_min = (int)Math.Floor(Math.Log(N - n) - Math.Log(Math.Log(N - n)));
                    L_max = (int)Math.Ceiling(Math.Log((N - n) * Math.Log(N - n), 2));
                    break;
                case 1:
                    L_min = 1;
                    L_max = 32;
                    break;
                case 2:
                    L_min = (int)Math.Ceiling(Math.Log(n * n, 2));
                    L_max = 32;
                    break;
                default:
                    L_min = 1;
                    L_max = 32;
                    break;
            }

            // 生成剩余标签
            List<string> remainingTags = GenerateBinaryStrings(N - n, BIT_LENGTH);
            List<Tuple<int, string, int, int>> candidateStrings = new List<Tuple<int, string, int, int>>();

            // 构建候选子串列表
            foreach (var (tag, index) in neededTags.Select((t, i) => (t, i)))
            {
                for (int length = L_min; length <= L_max; length++)
                {
                    for (int start = 0; start <= BIT_LENGTH - length; start++)
                    {
                        string substring = tag.Substring(start, length);
                        if (!IfExist(substring, remainingTags, start, start + length))
                        {
                            candidateStrings.Add(Tuple.Create(
                                index + 1,     // 标签索引
                                substring,    // 唯一子串
                                start + 1,    // 起始位置（1-based）
                                start + length // 结束位置
                            ));
                        }
                    }
                }
            }

            HashSet<int> neededTagList = new HashSet<int>(Enumerable.Range(1, n));
            int ectsBit = 0;
            int loopCount = 0;

            // ECTS算法处理流程
            while (neededTagList.Count > 0 && ECTS == 1)
            {
                loopCount++;
                
                // 统计最常出现的子串模式
                var mostCommon = candidateStrings
                    .GroupBy(x => new { x.Item2, x.Item3 })
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault();

                if (mostCommon == null) break;

                // 获取需要移除的标签索引
                var removedTags = new HashSet<int>(mostCommon.Select(x => x.Item1));
                ectsBit += mostCommon.Key.Item2.Length;

                // 更新待处理标签列表
                neededTagList.ExceptWith(removedTags);
                
                // 移除已处理的候选子串
                candidateStrings.RemoveAll(x => removedTags.Contains(x.Item1));
            }

            // 计算总比特开销（45为SELECT命令开销）
            int ectsBits = ectsBit + loopCount * 45;
            return (loopCount, ectsBits);
        }
    }

    class Program
    {
        // 测试配置参数
        const string TEST_NAME = "RFID_Performance_Test";
        static readonly string DATA_PATH = Path.Combine(
            @"D:\document\MATLAB\RFID", 
            DateTime.Now.ToString("yyyy-MM-dd"), 
            TEST_NAME);

        // 文件输出流
        static readonly StreamWriter DATA_WRITER = File.CreateText(Path.Combine(DATA_PATH, "results.dat"));
        
        // RFID读写器实例
        static ImpinjReader reader = new ImpinjReader();

        /// <summary>
        /// 主程序入口
        /// </summary>
        static void Main(string[] args)
        {
            try
            {
                // 初始化读写器连接
                reader.Connect("169.254.1.1");
                Settings settings = reader.QueryDefaultSettings();

                // 配置读写器参数
                settings.Report.Mode = ReportMode.Individual;
                settings.ReaderMode = ReaderMode.MaxThroughput;
                settings.Session = 2;
                settings.Antennas.GetAntenna(1).IsEnabled = true;

                reader.ApplySettings(settings);
                reader.TagsReported += OnTagsReported;

                // 启动计时器和读写器
                Common.Stopwatch.Start();
                Common.StartTime = Common.Stopwatch.ElapsedTicks;
                reader.Start();

                Console.WriteLine("测试运行中，按回车键退出...");
                Console.ReadLine();

                // 清理资源
                reader.Stop();
                Common.Stopwatch.Stop();
                DATA_WRITER.Close();
                reader.Disconnect();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"发生错误：{ex.Message}");
            }
        }

        /// <summary>
        /// 标签报告事件处理
        /// </summary>
        static void OnTagsReported(ImpinjReader sender, TagReport report)
        {
            foreach (Tag tag in report)
            {
                if (Common.SeenTags.Add(tag.Epc.ToString()))
                {
                    Common.UniqueTagCount++;
                    
                    // 计算时间间隔
                    long currentTicks = Common.Stopwatch.ElapsedTicks;
                    double interval = (currentTicks - Common.LastTagTime) * Common.TicksToMicroseconds - 10000;
                    Common.LastTagTime = currentTicks;

                    // 写入数据文件
                    DATA_WRITER.WriteLine(
                        $"{tag.Epc.ToString().Replace(" ", "")} " +
                        $"{Common.UniqueTagCount} " +
                        $"{interval:F1}");

                    // 当收集到100个标签时执行分析
                    if (Common.UniqueTagCount == 100)
                    {
                        var analysisResult = AnalysisTools.AnalyzeTags(
                            Common.SeenTags.ToList(), 
                            1000, 100, 
                            Limit: 1, 
                            ECTS: 1);

                        DATA_WRITER.WriteLine(
                            $"ECTS命令数: {analysisResult.ECTSSelect}\n" +
                            $"总比特数: {analysisResult.ECTSBits}");
                        
                        reader.Stop();
                        break;
                    }
                }
            }
            DATA_WRITER.Flush();
        }
    }
}