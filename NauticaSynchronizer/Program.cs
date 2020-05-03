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
		static string DownloadCount = "1/1";
		static string DownloadSpeed = "0B/s";
		static string DownloadProgress = "0.00%";
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
				DownloadCount = $"{SongIndex++}/{SongCount}";
				string SongID = SongData["id"].ToString();
				if (MetaTemp.TryGetValue(SongData["id"].ToString(), out string value))
				{
					string LoadLink = $"https://ksm.dev/songs/{SongID}/download";
					LogOut($"         id: {SongData["id"]}");
					LogOut($"      title: {SongData["title"]}");
					LogOut($"     artist: {SongData["artist"]}");
					LogOut($"uploaded_at: {SongData["uploaded_at"]}");
					LogOut($"   Download: {LoadLink}");
					LogOut($"    CDN_URL: {SongData["cdn_download_url"]}");
					LogOut($"Download From: {LoadLink}");
					CancellationTokenSource TokenSource = new CancellationTokenSource();
					Task<bool> completedTask = await Task.WhenAny(TimeoutDetector(30, TokenSource.Token), FileDownload(LoadLink, $"{SavePath}{SongID}.zip", TokenSource.Token));
					TokenSource.Cancel();
					TokenSource.Dispose();
					bool resultBool = completedTask.Result;
					if (!resultBool)
					{
						LoadLink = SongData["cdn_download_url"].ToString();
						LogOut($"Download From: {LoadLink}");
						CancellationTokenSource TokenSource1 = new CancellationTokenSource();
						Task<bool> completedTask1 = await Task.WhenAny(TimeoutDetector(30, TokenSource1.Token), FileDownload(LoadLink, $"{SavePath}{SongID}.zip", TokenSource1.Token));
						TokenSource1.Cancel();
						TokenSource1.Dispose();
						resultBool = completedTask1.Result;
					}
					if (resultBool)
					{
						try
						{
							// 解压文件
							Console.ForegroundColor = ConsoleColor.DarkYellow;
							LogOut("Extract...");
							Console.ForegroundColor = ConsoleColor.Gray;
							using FileStream zipFileToOpen = new FileStream($"{SavePath}{SongID}.zip", FileMode.Open);
							using ZipArchive archive = new ZipArchive(zipFileToOpen, ZipArchiveMode.Read);
							string ExtractDirectory = null;
							foreach (var currEntry in archive.Entries)
							{
								if (currEntry.FullName.Contains(@"/"))
								{
									ExtractDirectory = currEntry.FullName.Split('/')[0];
									break;
								}
							}
							archive.Dispose();
							zipFileToOpen.Close();
							// 防止文件名出现非法字符
							char[] invalidChars = Path.GetInvalidFileNameChars();
							string SaveDirectory = new string(SongData["title"].ToString().Where(x => !invalidChars.Contains(x)).ToArray()).Trim();
							string ExtractPath = ExtractDirectory != null ? SavePath : $"{SavePath}{SaveDirectory}";
							ExtractDirectory = ExtractDirectory != null ? $"{SavePath}{ExtractDirectory}" : $"{SavePath}{SaveDirectory}";
							Console.ForegroundColor = ConsoleColor.DarkYellow;
							LogOut($"  ExtractTo: {ExtractDirectory}");
							Console.ForegroundColor = ConsoleColor.Gray;
							if (Directory.Exists(ExtractDirectory)) Directory.Delete(ExtractDirectory, true);
							ZipFile.ExtractToDirectory($"{SavePath}{SongID}.zip", ExtractPath);
							File.Delete($"{SavePath}{SongID}.zip");
							// 保存记录
							LocalMeta[SongID] = SongData["uploaded_at"].ToString();
							DictionaryToFile(MetaPath, LocalMeta);
							// 操作成功
							Console.ForegroundColor = ConsoleColor.Green;
							LogOut("Success!");
							Console.ForegroundColor = ConsoleColor.Gray;
						}
						catch (Exception e)
						{
							Console.ForegroundColor = ConsoleColor.Red;
							LogOut($"Failed: {e.Message}");
							Console.ForegroundColor = ConsoleColor.Gray;
						}
					}
					else
					{
						Console.ForegroundColor = ConsoleColor.Red;
						LogOut("Failed!");
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
			string result;
			try
			{
				result = WClient.DownloadString(FileLink);
			}
			catch (WebException e)
			{
				Console.WriteLine($"{e.Message}, Try Again...");
				result = null;
			}
			if (result == null)
			{
				try
				{
					result = WClient.DownloadString(FileLink);
				}
				catch (WebException e)
				{
					Console.WriteLine(e.Message);
					result = null;
				}
			}
			return result;
		}

		/// <summary>
		/// 下载文件
		/// </summary>
		/// <param name="FileLink">目标链接</param>
		/// <param name="FilePath">保存路径</param>
		/// <returns>操作结果</returns>
		static async Task<bool> FileDownload(string FileLink, string FilePath, CancellationToken CancelToken)
		{
			bool result;
			ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;          // SecurityProtocolType.Tls1.2;
			ServicePointManager.DefaultConnectionLimit = 20;
			Title = $"NauticaSynchronizer  -  Count:{DownloadCount} , Speed:0B/s , Progress:0.00% , Loaded:0Byte";

			List<byte> buffers = new List<byte>();
			HttpWebRequest Downloader = (HttpWebRequest)WebRequest.Create(FileLink);
			Downloader.KeepAlive = false;
			Downloader.Method = "GET";
			Downloader.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/81.0.4044.129 Safari/537.36";
			Downloader.ServicePoint.Expect100Continue = false;
			Downloader.Timeout = 30000;
			Downloader.ReadWriteTimeout = 60000;
			try
			{
				using HttpWebResponse response = (HttpWebResponse)Downloader.GetResponse();
				long FileSize = response.ContentLength;
				long SaveSize = 0;
				long TickSize = 0;

				using Stream ns = response.GetResponseStream();
				int readSize = 0;
				int buffSize = 1024;
				byte[] buffer = new byte[buffSize];
				DateTime ReferTime = DateTime.Now.AddSeconds(1);
				do
				{
					readSize = await ns.ReadAsync(buffer, 0, buffSize);
					buffers.AddRange(buffer);
					SaveSize += readSize;
					TickSize += readSize;
					if (CancelToken.IsCancellationRequested)
					{
						Downloader.Abort();
						buffers.Clear();
						return false;
					}

					if (TickSize == SaveSize || DateTime.Compare(ReferTime, DateTime.Now) <= 0)
					{
						if (TickSize >= 1024 * 1024)
						{
							DownloadSpeed = $"{(double)TickSize / 1024 / 1024:0.00}M/s";
						}
						else if (TickSize >= 1024)
						{
							DownloadSpeed = $"{(double)TickSize / 1024:0.00}K/s";
						}
						else
						{
							DownloadSpeed = $"{TickSize}B/s";
						}
						if (DateTime.Compare(ReferTime, DateTime.Now) <= 0)
						{
							TickSize = 0;
							ReferTime = ReferTime.AddSeconds(1);
						}
					}
					DownloadProgress = FileSize > 0 ? $"{(double)SaveSize * 100 / FileSize:0.00}%" : $"{(double)SaveSize / 1024:0.00}KB";
					if (FileSize > 1024 * 1024) DownloadProgress = $"{DownloadProgress} , Loaded:{(double)SaveSize / 1024 / 1024:0.00}MB";
					else if (FileSize > 1024) DownloadProgress = $"{DownloadProgress} , Loaded:{(double)SaveSize / 1024:0.00}KB";

					Title = $"NauticaSynchronizer  -  Count:{DownloadCount} , Speed:{DownloadSpeed} , Progress:{DownloadProgress}";
				}
				while (readSize > 0);

				File.WriteAllBytes(FilePath, buffers.ToArray());
				ns.Close();
				response.Close();
				result = true;
			}
			catch (Exception e)
			{
				Console.WriteLine(e.Message);
				result = false;
			}
			// Free up resources
			if (Downloader != null) Downloader.Abort();
			buffers.Clear();
			return result;
		}
		/// <summary>
		/// Task超时判断任务
		/// </summary>
		/// <param name="time">设置时间阈值(ms)</param>
		/// <returns></returns>
		static async Task<bool> TimeoutDetector(int time, CancellationToken CancelToken)
		{
			int tick = 0;
			string refer = Title;
			while (tick < time)
			{
				await Task.Delay(1000);
				if (refer == Title)
				{
					++tick;
				}
				else
				{
					tick = 0;
					refer = Title;
				}
				if (CancelToken.IsCancellationRequested)
				{
					return true;
				}
			}

			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine("Task Execution Timeout!");
			Console.ForegroundColor = ConsoleColor.Gray;
			return false;
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
