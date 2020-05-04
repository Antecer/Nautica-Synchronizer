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

namespace NauticaSynchronizer
{
	class Program
	{
		private static readonly SemaphoreSlim _mutex = new SemaphoreSlim(10);
		private static string Title { get { return Console.Title; } set { Console.Title = value; } }
		static List<Dictionary<string, object>> MetaTable = new List<Dictionary<string, object>>();
		static void Main(string[] args)
		{
			Title = "NauticaSynchronizer";
			_ = AsyncMain();
			Console.ReadKey();
		}
		static async Task<int> AsyncMain()
		{
			string AppPath = AppDomain.CurrentDomain.SetupInformation.ApplicationBase;
			string ListLink = "https://ksm.dev/app/songs";
			string ListPath = AppPath + "temp.json";
			string SavePath = AppPath.EndsWith(@"songs\") ? AppPath : AppPath + @"songs\";
			string MetaPath = SavePath + "meta.cfg";
			if (!Directory.Exists(SavePath))
			{
				Directory.CreateDirectory(SavePath);
			}

			LogOut("Get Nautica Meta From[https://ksm.dev] ...");
			string MetaData = GetHtml(ListLink);
			if (MetaData == null)
			{
				LogOut($"Get NauticaMeta[{ListLink}] Failed!");
				return 0;
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
						Console.ForegroundColor = ConsoleColor.Red;
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
					Title = $"NauticaSynchronizer  -  Count:{pageIndex++}/{pageCount}";

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
			Dictionary<string, string> MetaTemp = new Dictionary<string, string>();
			if (File.Exists(MetaPath))
			{
				MetaTemp = FileToDictionary(MetaPath);
				LocalMeta = new Dictionary<string, string>(MetaTemp);
				foreach (var SongData in MetaTable)
				{
					string SongID = SongData["id"].ToString();
					if (MetaTemp.TryGetValue(SongID, out string value))
					{
						if (SongData["uploaded_at"].ToString() == value)
						{
							MetaTemp.Remove(SongID);
						}
						else
						{
							LocalMeta.Remove(SongID);
						}
					}
					else
					{
						MetaTemp[SongID] = SongData["uploaded_at"].ToString();
					}
				}
				if (MetaTemp.Count == 0)
				{
					LogOut("The music library is already in the latest version!");
					return 0;
				}
			}
			else
			{
				foreach (var SongData in MetaTable)
				{
					MetaTemp[SongData["id"].ToString()] = SongData["uploaded_at"].ToString();
				}
			}
			Console.ForegroundColor = ConsoleColor.Green;
			Console.WriteLine("Success!\n");
			Console.ForegroundColor = ConsoleColor.Gray;

			LogOut("Update Music Library ...");
			int SongCount = MetaTable.Count;
			int SongIndex = 1;
			foreach (var SongData in MetaTable)
			{
				string SongID = SongData["id"].ToString();
				if (MetaTemp.TryGetValue(SongID, out string value))
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
					Downloader WebLoader = new Downloader("NauticaSynchronizer", $"{SongIndex++}/{SongCount}");
					byte[] WebFileBuffer = await WebLoader.GetWebFileBuffer(LoadLink, 10);
					if (WebFileBuffer.Length < 1)
					{
						LoadLink = SongData["cdn_download_url"].ToString();
						Console.ForegroundColor = ConsoleColor.DarkYellow;
						Console.WriteLine($"   Get_From: {LoadLink}");
						Console.ForegroundColor = ConsoleColor.Gray;
						WebFileBuffer = await WebLoader.GetWebFileBuffer(LoadLink, 10);
					}
					if (0 < WebFileBuffer.Length)
					{
						try
						{
							// 解压文件
							Console.ForegroundColor = ConsoleColor.DarkYellow;
							LogOut("Extract...");
							Console.ForegroundColor = ConsoleColor.Gray;
							using Stream MemStream = new MemoryStream(WebFileBuffer);
							using ZipArchive archive = new ZipArchive(MemStream, ZipArchiveMode.Read);
							string ExtractToPath = $"{SavePath}{SongID}";// 解压到SongID文件夹
							Directory.CreateDirectory(ExtractToPath);
							foreach (ZipArchiveEntry currEntry in archive.Entries)
							{
								if(currEntry.Name.Length>0)
								{
									currEntry.ExtractToFile($"{ExtractToPath}/{currEntry.Name}", true);
								}
							}
							Console.ForegroundColor = ConsoleColor.DarkYellow;
							LogOut($"  ExtractTo: {ExtractToPath}");
							Console.ForegroundColor = ConsoleColor.Gray;
							// 保存记录
							LocalMeta[SongID] = $"{SongData["uploaded_at"]}|{SongData["title"]}|{SongData["artist"]}";
							DictionaryToFile(MetaPath, LocalMeta);
							// 操作成功
							Console.ForegroundColor = ConsoleColor.Green;
							LogOut("Success!");
							Console.ForegroundColor = ConsoleColor.Gray;
						}
						catch (Exception e)
						{
							Console.ForegroundColor = ConsoleColor.Red;
							Console.WriteLine($"Failed: {e.Message}");
							Console.ForegroundColor = ConsoleColor.Gray;
							// 解压失败时保存zip文件
							File.WriteAllBytes($"{SavePath}/{SongID}.zip", WebFileBuffer);
						}
					}
					else
					{
						Console.ForegroundColor = ConsoleColor.Red;
						Console.WriteLine("Failed!");
						Console.ForegroundColor = ConsoleColor.Gray;
					}
					LogOut("");
				}
			}
			return 0;
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
					if (string.IsNullOrWhiteSpace(line)) break;
					string[] kv = line.Split('|');
					result[kv[0]] = kv[1];
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
			List<string> lines = new List<string>();
			foreach (string k in dic.Keys)
			{
				lines.Add($"{k}|{dic[k]}");
			}
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
