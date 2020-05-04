using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NauticaSynchronizer
{
	class Downloader
	{
		public string Title { get; set; }
		public string Count { get; set; }
		public string Speed { get; set; }
		public string Track { get; set; }
		public string Cache { get; set; }
		public Downloader(string title = "Console", string count = "1/1")
		{
			Title = title;
			Count = count;
			Speed = "0B/s";
			Track = "0.00%";
			Cache = "0Byte";
		}
		private long FileSize = 0;
		private List<byte> FileBuffer = new List<byte>();

		/// <summary>
		/// 超时定时器
		/// </summary>
		/// <param name="CancelToken"></param>
		/// <param name="TimeOut"></param>
		/// <returns></returns>
		private async Task<bool> TimeoutTimer(CancellationToken CancelToken, int TimeOut)
		{
			int TimeNow = 0;
			long SaveSize = FileBuffer.Count;
			long TickSize;
			while (TimeNow < TimeOut)
			{
				await Task.Delay(1000);
				TickSize = FileBuffer.Count - SaveSize;
				Speed = TickSize > Math.Pow(2, 20) ? $"{TickSize / Math.Pow(2, 20):0.00}M/s" : $"{TickSize / Math.Pow(2, 10):0.00}K/s";
				Console.Title = $"{Title}  -  Count:{Count} , Speed:{Speed} , Progress:{Track} , Loaded:{Cache}";
				++TimeNow;
				if (SaveSize < FileBuffer.Count)
				{
					TimeNow = 0;
					SaveSize = FileBuffer.Count;
				}
				if (CancelToken.IsCancellationRequested) return true;
			}
			Console.ForegroundColor = ConsoleColor.DarkYellow;
			Console.WriteLine("Task execution timeout!");
			Console.ForegroundColor = ConsoleColor.Gray;
			return false;
		}
		/// <summary>
		/// 获取网络文件数据存入FileBuffer
		/// </summary>
		/// <param name="CancelToken"></param>
		/// <param name="FileLink"></param>
		/// <returns></returns>
		private async Task<bool> GetWebData(CancellationToken CancelToken, string FileLink)
		{
			bool result = false;
			Console.Title = $"{Title}  -  Count:{Count} , Speed:{Speed} , Progress:{Track} , Loaded:{Cache}";

			ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
			ServicePointManager.DefaultConnectionLimit = 20;
			HttpWebRequest Downloader = (HttpWebRequest)WebRequest.Create(FileLink);
			Downloader.KeepAlive = false;
			Downloader.Method = "GET";
			Downloader.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/81.0.4044.129 Safari/537.36";
			Downloader.ServicePoint.Expect100Continue = false;
			Downloader.Timeout = 30000;
			try
			{
				using HttpWebResponse response = (HttpWebResponse)Downloader.GetResponse();
				FileSize = response.ContentLength;
				using Stream WebStream = response.GetResponseStream();
				long LoadSize = 0;
				int readSize = 0;
				int buffSize = 1024;
				byte[] buffer = new byte[buffSize];
				do
				{
					readSize = await WebStream.ReadAsync(buffer, 0, buffSize);
					if (CancelToken.IsCancellationRequested)
					{
						Downloader.Abort();
						return false;
					}
					FileBuffer.AddRange(buffer);

					LoadSize += readSize;
					Track = FileSize > 0 ? $"{(double)LoadSize * 100 / FileSize:0.00}%" : "unknown";
					if (LoadSize >= Math.Pow(2, 30)) Cache = $"{LoadSize / Math.Pow(2, 30):0.00}GB";
					else if (LoadSize >= Math.Pow(2, 20)) Cache = $"{LoadSize / Math.Pow(2, 20):0.00}MB";
					else if (LoadSize >= Math.Pow(2, 10)) Cache = $"{LoadSize / Math.Pow(2, 10)}KB";
					else Cache = $"{LoadSize}Byte";
					Console.Title = $"{Title}  -  Count:{Count} , Speed:{Speed} , Progress:{Track} , Loaded:{Cache}";
				}
				while (readSize > 0);
				result = true;
			}
			catch (Exception e)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine(e.Message);
				Console.ForegroundColor = ConsoleColor.Gray;
			}
			Downloader.Abort();
			return result;
		}
		/// <summary>
		/// 获取网络文件ListBuffer
		/// </summary>
		/// <param name="FileLink">目标链接</param>
		/// <param name="TimeOut">通信超时(秒)</param>
		/// <returns>操作结果</returns>
		private async Task<bool> GetWebBuffer(string FileLink, int TimeOut = 10)
		{
			Console.Title = $"{Title}  -  Count:{Count} , Speed:{Speed} , Progress:{Track} , Loaded:{Cache}";
			CancellationTokenSource TokenSource = new CancellationTokenSource();
			CancellationToken CancelToken = TokenSource.Token;

			Task<bool> CompletedTask = await Task.WhenAny(TimeoutTimer(CancelToken, TimeOut), GetWebData(CancelToken, FileLink));
			TokenSource.Cancel();
			return CompletedTask.Result;
		}
		/// <summary>
		/// 获取网络文件存入byte[]
		/// </summary>
		/// <param name="FileLink">Web文件链接</param>
		/// <param name="TimeOut">超时限制(秒)</param>
		/// <returns>成功:返回文件byte[], 失败:返回new byte[0];</returns>
		public async Task<byte[]> GetWebFileBuffer(string FileLink, int TimeOut = 10)
		{
			byte[] WebFileBuffer = await GetWebBuffer(FileLink, TimeOut) ? FileBuffer.ToArray() : new byte[0];
			return WebFileBuffer;
		}
		/// <summary>
		/// 获取网络文件存入Stream
		/// </summary>
		/// <param name="FileLink">Web文件链接</param>
		/// <param name="TimeOut">超时限制(秒)</param>
		/// <returns>成功:返回文件Stream, 失败:返回空Stream;</returns>
		public async Task<Stream> GetWebFileStream(string FileLink, int TimeOut = 10)
		{
			using Stream WebFileStream = new MemoryStream(await GetWebFileBuffer(FileLink, TimeOut));
			return WebFileStream;
		}
		/// <summary>
		/// 获取网页Html源码
		/// </summary>
		/// <param name="FileLink">网页链接</param>
		/// <param name="TimeOut">超时限制(秒)</param>
		/// <returns>成功:返回网页源码, 失败:返回Null;</returns>
		public async Task<string> GetWebFileString(string FileLink, int TimeOut = 10)
		{
			using Stream WebFileStream = new MemoryStream(await GetWebFileBuffer(FileLink, TimeOut));
			if (WebFileStream.Length > 0)
			{
				StreamReader reader = new StreamReader(WebFileStream);
				return reader.ReadToEnd();
			}
			return null;
		}

		/// <summary>
		/// 下载文件并存入指定路径
		/// </summary>
		/// <param name="FileLink">文件链接</param>
		/// <param name="SavePath">保存路径</param>
		/// <param name="TimeOut">超时限制(秒)</param>
		/// <returns>成功:返回True, 失败:返回False;</returns>
		public async Task<bool> Download(string FileLink, string SavePath, int TimeOut = 10)
		{
			try
			{
				File.WriteAllBytes(SavePath, await GetWebFileBuffer(FileLink, TimeOut));
				return true;
			}
			catch (Exception e)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine(e.Message);
				Console.ForegroundColor = ConsoleColor.Gray;
				return false;
			}
		}
	}
}
