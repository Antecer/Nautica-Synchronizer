using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Controls;
using System.Windows.Documents;

namespace NauticaSynchronizer
{
	class Program
	{
		private static readonly SemaphoreSlim _mutex = new SemaphoreSlim(10);
		private static readonly List<Dictionary<string, object>> MetaTable = new List<Dictionary<string, object>>();
		private static int ExtractMode = 1;
		static void Main(string[] args)
		{
			Task.WaitAny(MainTaskAsync());
		}

		static async Task MainTaskAsync()
		{
			Console.Title = "NauticaSynchronizer";

			Console.WriteLine("Please select a naming method for the extracted folder:");
			Console.WriteLine(@"1. Extract to 'SongId\' (default)");
			Console.WriteLine(@"2. Extract to 'SongTitle\'");
			Console.WriteLine(@"3. Extract to 'SongTitle - Artist\'");
			Console.WriteLine(@"0. Do not extract zip!");
			DateTime WaitTime = DateTime.Now.AddSeconds(10);
			while (!Console.KeyAvailable)
			{
				await Task.Delay(100);
				Console.CursorLeft = 0;
				Console.Write($"Selected[wait:{(WaitTime - DateTime.Now).Seconds}s]:");
				if ((WaitTime - DateTime.Now).Seconds < 1) break;
			}
			ConsoleKey UserInputKey = Console.KeyAvailable ? Console.ReadKey().Key : ConsoleKey.D1;
			ExtractMode = UserInputKey switch
			{
				ConsoleKey.D0 => 0,
				ConsoleKey.D1 => 1,
				ConsoleKey.D2 => 2,
				ConsoleKey.D3 => 3,
				_ => 1,
			};
			Console.CursorLeft = 0;
			Console.WriteLine($"Selected: {ExtractMode}{new string(' ', 10)}\n");

			while (true)
			{
				Task<int> MainTask = await Task.WhenAny(AsyncMain());
				if (MainTask.Result == 0)
				{
					Console.Write("Press any key to exit.");
					Console.ReadKey(true);
					break;
				}
				else
				{
					Console.Write("Press any key to try again, or press Esc to exit?");
					if (Console.ReadKey(true).Key == ConsoleKey.Escape) break;
					Console.Clear();
				}
			}
		}

		static async Task<int> AsyncMain()
		{
			string AppPath = AppDomain.CurrentDomain.SetupInformation.ApplicationBase;
			string ListLink = "https://ksm.dev/app/songs";
			string SavePath = "";
			if (AppPath.EndsWith(@"Nautica\")) SavePath = AppPath;
			else if (AppPath.EndsWith(@"songs\")) SavePath = $@"{AppPath}Nautica\";
			else if (Directory.Exists($"{AppPath}songs")) SavePath = $@"{AppPath}songs\Nautica\";
			else if (File.Exists($"{AppPath}usc-game.exe")) SavePath = $@"{AppPath}songs\Nautica\";
			else SavePath = $@"{AppPath}Nautica\";
			string MetaPath = $@"{SavePath}meta.cfg";
			Directory.CreateDirectory(SavePath);

			LogOut(@"Get Nautica Meta From <https://ksm.dev> ...");
			string MetaData = GetHtml(ListLink);
			if (MetaData == null)
			{
				LogOut($"Get NauticaMeta[{ListLink}] Failed!");
				return -1;
			}
			Dictionary<string, object> meta = JsonToDictionary(MetaData)["meta"] as Dictionary<string, object>;
			int pageCount = (int)meta["last_page"];
			int pageIndex = 1;
			object locker = new object();
			var task = Enumerable.Range(1, pageCount).Select(i =>
				Task.Run(async () =>
				{
					await _mutex.WaitAsync();

					string getlink = $"{ListLink}?page={i}";
					string MetaJson = GetHtml(getlink);
					if (string.IsNullOrWhiteSpace(MetaJson))
					{
						Console.ForegroundColor = ConsoleColor.DarkRed;
						Console.WriteLine($"Get({getlink}) Failed!");
						Console.ForegroundColor = ConsoleColor.Gray;
					}
					else
					{
						ArrayList MetaArray = JsonToDictionary(MetaJson)["data"] as ArrayList;
						foreach (var m in MetaArray)
						{
							lock (locker)
							{
								MetaTable.Add(m as Dictionary<string, object>);
							}
						}
					}
					Console.Title = $"NauticaSynchronizer  -  Count:{pageIndex++}/{pageCount}";

					_mutex.Release();
				})
			).ToList();
			await Task.WhenAll(task);
			Console.ForegroundColor = ConsoleColor.Green;
			Console.WriteLine("Success!\n");
			Console.ForegroundColor = ConsoleColor.Gray;

			LogOut("Analyze Nautica Meta ...");
			int total = (int)meta["total"];
			Dictionary<string, string> LocalMeta = new Dictionary<string, string>();
			if (File.Exists(MetaPath))
			{
				LocalMeta = FileToDictionary(MetaPath);
				foreach (var SongData in MetaTable)
				{
					string SongID = SongData["id"].ToString();
					if (LocalMeta.TryGetValue(SongID, out string value))
					{
						if (value.Contains($"{SongData["uploaded_at"]}"))
						{
							LocalMeta[SongID] = $"{SongID}|{SongData["uploaded_at"]}|{SongData["title"]}|{SongData["artist"]}";
						}
						else
						{
							LocalMeta.Remove(SongID);
						}
					}
				}
				if (LocalMeta.Count == MetaTable.Count)
				{
					LogOut("Nautica music library is already in the latest version!");
					return 0;
				}
			}
			Console.ForegroundColor = ConsoleColor.Green;
			Console.WriteLine("Success!\n");
			Console.ForegroundColor = ConsoleColor.Gray;

			LogOut("Update Music Library ...");
			int SongCount = MetaTable.Count;
			int SongIndex = 0;
			int GoodCount = 0;
			int FailCount = 0;
			foreach (var SongData in MetaTable)
			{
				++SongIndex;
				Console.Title = $"NauticaSynchronizer  -  Count:{SongIndex}/{SongCount}";
				string SongID = SongData["id"].ToString();
				if (SongID.Length < 9) continue;	// 过滤id长度不符合要求的数据
				if (!LocalMeta.ContainsKey(SongID))
				{
					string LoadLink = $"https://ksm.dev/songs/{SongID}/download";
					LogOut($"         id: {SongID}");
					LogOut($"      title: {SongData["title"]}");
					LogOut($"     artist: {SongData["artist"]}");
					LogOut($"uploaded_at: {SongData["uploaded_at"]}");
					LogOut($"   Download: {LoadLink}");
					LogOut($"    CDN_URL: {SongData["cdn_download_url"]}");
					Console.ForegroundColor = ConsoleColor.DarkYellow;
					Console.WriteLine($"   Get_From: {LoadLink}");
					Console.ForegroundColor = ConsoleColor.Gray;
					Downloader WebLoader = new Downloader("NauticaSynchronizer", $"{SongIndex}/{SongCount}");
					byte[] WebFileBuffer = await WebLoader.GetWebFileBuffer(LoadLink, 10);
					if (WebFileBuffer.Length < 1)
					{
						LoadLink = SongData["cdn_download_url"].ToString();
						Console.ForegroundColor = ConsoleColor.DarkYellow;
						Console.WriteLine($"   Get_From: {LoadLink}");
						Console.ForegroundColor = ConsoleColor.Gray;
						WebFileBuffer = await WebLoader.GetWebFileBuffer(LoadLink, 10);
					}
					if (WebFileBuffer.Length < 1)
					{
						Console.ForegroundColor = ConsoleColor.DarkRed;
						Console.WriteLine("Failed!\n");
						Console.ForegroundColor = ConsoleColor.Gray;
						++FailCount;
						continue;
					}
					try
					{
						if (0 < ExtractMode)
						{
							// 设置释放目标文件夹命名方式
							string ExtractFolder = ExtractMode switch
							{
								1 => $"{SongData["id"]}",
								2 => $"{SongData["title"]}",
								3 => $"{SongData["title"]} - {SongData["artist"]}",
								_ => $"{SongData["id"]}"
							};
							// 解压文件
							Console.ForegroundColor = ConsoleColor.DarkYellow;
							LogOut("Extract...");
							Console.ForegroundColor = ConsoleColor.Gray;
							using Stream MemStream = new MemoryStream(WebFileBuffer);
							using ZipArchive archive = new ZipArchive(MemStream, ZipArchiveMode.Read);
							string ExtractToPath = $"{SavePath}{ExtractMode}";// 解压到SongID文件夹
							Directory.CreateDirectory(ExtractToPath);
							foreach (ZipArchiveEntry currEntry in archive.Entries)
							{
								if (currEntry.Name.Trim().Length > 0)
								{
									currEntry.ExtractToFile($"{ExtractToPath}/{currEntry.Name}", true);
								}
							}
							Console.ForegroundColor = ConsoleColor.DarkYellow;
							LogOut($"  ExtractTo: {ExtractToPath}");
							Console.ForegroundColor = ConsoleColor.Gray;
						}
						// 保存记录
						LocalMeta[$"{SongID}"] = $"{SongID}|{SongData["uploaded_at"]}|{SongData["title"]}|{SongData["artist"]}";
						DictionaryToFile(MetaPath, LocalMeta);
						// 操作成功
						Console.ForegroundColor = ConsoleColor.Green;
						LogOut("Success!\n");
						Console.ForegroundColor = ConsoleColor.Gray;
						++GoodCount;
					}
					catch (Exception e)
					{
						Console.ForegroundColor = ConsoleColor.DarkRed;
						Console.WriteLine($"Failed: {e.Message}\n");
						Console.ForegroundColor = ConsoleColor.Gray;
						++FailCount;
						// 解压失败时保存zip文件
						File.WriteAllBytes($"{SavePath}/{SongID}.zip", WebFileBuffer);
					}
				}
			}

			Console.Title = $"NauticaSynchronizer  -  Count:{SongIndex}/{SongCount}";
			DictionaryToFile(MetaPath, LocalMeta);
			Console.ForegroundColor = ConsoleColor.Cyan;
			LogOut($"Nautica music library update completed: Total={SongCount},Good={GoodCount},Pass={SongCount - GoodCount - FailCount},Fail={FailCount}");
			Console.ForegroundColor = ConsoleColor.Gray;

			return FailCount == 0 ? 0 : 1;
		}

		/// <summary>
		/// 输出日志到控制台
		/// </summary>
		/// <param name="log">日志信息</param>
		static void LogOut(string log)
		{
			Console.WriteLine(log);
		}

		/// <summary>
		/// 获取网页
		/// </summary>
		/// <param name="FileLink">目标链接</param>
		/// <returns>网页源码</returns>
		static string GetHtml(string FileLink)
		{
			ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;          // SecurityProtocolType.Tls1.2;
			ServicePointManager.DefaultConnectionLimit = 20;
			WebClient WClient = new WebClient();
			string result = null;
			int tryCount = 2;
			while (tryCount-- > 0)
			{
				try
				{
					result = WClient.DownloadString(FileLink);
					break;
				}
				catch (Exception e)
				{
					Console.WriteLine($"{e.Message}, Try Again...");
				}
			}
			return result;
		}

		/// <summary>
		/// 将Json数据反序列化为Dictionary字典
		/// </summary>
		/// <param name="jsonData">json数据</param>
		/// <returns>Dictionary字典</returns>
		static Dictionary<string, object> JsonToDictionary(string jsonData)
		{
			JavaScriptSerializer jss = new JavaScriptSerializer();
			try
			{
				return jss.Deserialize<Dictionary<string, object>>(jsonData);
			}
			catch (Exception e)
			{
				throw new Exception(e.Message);
			}
		}

		/// <summary>
		/// 从文本文档读取字典数据
		/// </summary>
		/// <param name="FilePath">文件路径</param>
		/// <returns>字典数据</returns>
		static Dictionary<string, string> FileToDictionary(string FilePath)
		{
			Dictionary<string, string> result = new Dictionary<string, string>();
			try
			{
				List<string> lines = new List<string>(File.ReadAllLines(FilePath));
				foreach (string line in lines)
				{
					if (string.IsNullOrWhiteSpace(line)) continue;
					else result[line.Split('|')[0]] = line;
				}
			}
			catch (Exception e)
			{
				Console.WriteLine(e.Message);
			}
			return result;
		}
		/// <summary>
		/// 将字典保存到文本文档
		/// </summary>
		/// <param name="FilePath">文件路径</param>
		/// <param name="dic">字典数据</param>
		/// <returns>操作结果</returns>
		static bool DictionaryToFile(string FilePath, Dictionary<string, string> dic)
		{
			List<string> lines = new List<string>(dic.Values);
			try
			{
				File.WriteAllLines(FilePath, lines);
			}
			catch (Exception e)
			{
				Console.WriteLine(e.Message);
				return false;
			}
			return true;
		}
	}
}
