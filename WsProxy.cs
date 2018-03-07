using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;

using System.Net.WebSockets;
using System.Threading;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace WsProxy {

	public struct Result {
		public JObject Value { get; private set; }
		public JObject Error { get; private set; }

		public bool IsOk => Value != null;
		public bool IsErr => Error != null;

		Result (JObject result, JObject error)
		{
			this.Value = result;
			this.Error = error;
		}

		public static Result FromJson (JObject obj)
		{
			return new Result (obj ["result"] as JObject, obj ["error"] as JObject);
		}

		public static Result Ok (JObject ok)
		{
			return new Result (ok, null);
		}

		public static Result Err (JObject err)
		{
			return new Result (null, err);
		}

		public JObject ToJObject (int id) {
			if (IsOk) {
				return JObject.FromObject (new {
					id = id,
					result = Value
				});
			} else {
				return JObject.FromObject (new {
					id = id,
					error = Error
				});
			}
		}
	}


	public class WsProxy {
		List<(int, TaskCompletionSource<Result>)> pending_cmds = new List<(int, TaskCompletionSource<Result>)> ();
		ClientWebSocket browser;
		WebSocket ide;
		int next_cmd_id;

		protected virtual Task<bool> AcceptEvent (string method, JObject args, CancellationToken token)
		{
			return Task.FromResult (false);
		}

		protected virtual Task<bool> AcceptCommand (int id, string method, JObject args, CancellationToken token)
		{
			return Task.FromResult (false);
		}

		Uri GetBrowserUri (string path)
		{
			return new Uri ("ws://localhost:9222" + path);
		}

		async Task<string> ReadOne (WebSocket socket, CancellationToken token)
		{
			byte [] buff = new byte [4000];
			var mem = new MemoryStream ();
			while (true) {
				var result = await socket.ReceiveAsync (new ArraySegment<byte> (buff), token);
				if (result.MessageType == WebSocketMessageType.Close) {
					return null;
				}

				if (result.EndOfMessage) {
					mem.Write (buff, 0, result.Count);
					return Encoding.UTF8.GetString (mem.GetBuffer (), 0, (int)mem.Length);
				} else {
					mem.Write (buff, 0, result.Count);
				}
			}
		}

		async Task Send (WebSocket to, JObject o, CancellationToken token)
		{
			var bytes = Encoding.UTF8.GetBytes (o.ToString ());
			await to.SendAsync (new ArraySegment<byte> (bytes), WebSocketMessageType.Text, true, token);
		}

		async void OnEvent (string method, JObject args, CancellationToken token)
		{
			if (!await AcceptEvent (method, args, token))
				await SendEventInternal (method, args, token);
		}

		async void OnCommand (int id, string method, JObject args, CancellationToken token)
		{
			if (!await AcceptCommand (id, method, args, token)) {
				var res = await SendCommandInternal (method, args, token);
				await SendResponseInternal (id, res, token);
			}
		}

		void OnResponse (int id, Result result)
		{
			var idx = pending_cmds.FindIndex (e => e.Item1 == id);
			var item = pending_cmds [idx];
			pending_cmds.RemoveAt (idx);

			item.Item2.SetResult (result);
		}

		async Task ReadFromBrowser (CancellationToken token)
		{
			string msg;
			while ((msg = await ReadOne (browser, token)) != null) {
				Debug ($"browser: {msg}");
				var res = JObject.Parse (msg);

				if (res ["id"] == null)
					OnEvent (res ["method"].Value<string> (), res ["params"] as JObject, token);
				else
					OnResponse (res ["id"].Value<int> (), Result.FromJson (res));
			}
		}

		async Task ReadFromIde (CancellationToken token)
		{
			string msg;
			while ((msg = await ReadOne (ide, token)) != null) {
				Debug ($"ide: {msg}");
				var res = JObject.Parse (msg);

				OnCommand (res ["id"].Value<int> (), res ["method"].Value<string> (), res ["params"] as JObject, token);
			}
		}

		public async Task<Result> SendCommand (string method, JObject args, CancellationToken token) {
			Debug ($"sending command {method}: {args}");
			return await SendCommandInternal (method, args, token);
		}

		async Task<Result> SendCommandInternal (string method, JObject args, CancellationToken token)
		{
			int id = ++next_cmd_id;

			var o = JObject.FromObject (new {
				id = id,
				method = method,
				@params = args
			});
			await Send (this.browser, o, token);

			var tcs = new TaskCompletionSource<Result> ();
			pending_cmds.Add ((id, tcs));

			return await tcs.Task;
		}

		public async Task SendEvent (string method, JObject args, CancellationToken token)
		{
			Debug ($"sending event {method}: {args}");
			await SendEventInternal (method, args, token);
		}

		async Task SendEventInternal (string method, JObject args, CancellationToken token)
		{
			var o = JObject.FromObject (new {
				method = method,
				@params = args
			});

			await Send (this.ide, o, token);
		}

		public async Task SendResponse (int id, Result result, CancellationToken token)
		{
			Debug ($"sending response: {id}: {result.ToJObject (id)}");
			await SendResponseInternal (id, result, token);
		}

		async Task SendResponseInternal (int id, Result result, CancellationToken token)
		{
			JObject o = result.ToJObject (id);

			await Send (this.ide, o, token);
		}

		public async Task Run (HttpContext context)
		{
			var browserUri = GetBrowserUri (context.Request.Path.ToString ());
			Debug ("wsproxy start");
			using (this.ide = await context.WebSockets.AcceptWebSocketAsync ()) {
				Debug ("ide connected");
				using (this.browser = new ClientWebSocket ()) {
					this.browser.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
					await this.browser.ConnectAsync (browserUri, CancellationToken.None);

					Debug ("client connected");
					var x = new CancellationTokenSource ();

					try {
						var a = ReadFromBrowser (x.Token);
						var b = ReadFromIde (x.Token);

						await Task.WhenAny (a, b);
					} catch (Exception e) {
						Debug ($"got exception {e}");
						//throw;
					} finally {
						x.Cancel ();
					}
				}
			}
		}

		protected void Debug (string msg)
		{
			Console.WriteLine (msg);
		}

		protected void Info (string msg)
		{
			Console.WriteLine (msg);
		}
	}
}
