using System;
using System.Linq;
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
		TaskCompletionSource<bool> side_exception = new TaskCompletionSource<bool> ();
		List<(int, TaskCompletionSource<Result>)> pending_cmds = new List<(int, TaskCompletionSource<Result>)> ();
		ClientWebSocket browser;
		WebSocket ide;
		int next_cmd_id;
		List<Task> pending_ops = new List<Task> ();
		List<WsQueue> queues = new List<WsQueue> ();

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

		class WsQueue {
			Task current_send;
			List<Tuple<TaskCompletionSource<bool>, byte []>> pending;

			public WebSocket Ws { get; private set; }
			public Task CurrentSend { get { return current_send; } }
			public WsQueue (WebSocket sock) {
				this.Ws = sock;
				pending = new List<Tuple<TaskCompletionSource<bool>, byte []>> ();
			}

			public Task Send (byte[] bytes, CancellationToken token) {
				var tuple = Tuple.Create (new TaskCompletionSource<bool> (), bytes);
				pending.Add (tuple);
				if (pending.Count == 1) {
					if (current_send != null)
						throw new Exception ("WTF, current_send MUST BE NULL IF THERE'S no pending send");
					current_send = Ws.SendAsync (new ArraySegment<byte> (bytes), WebSocketMessageType.Text, true, token);
				}
				return tuple.Item1.Task;
			}

			public Task Pump (CancellationToken token) {
				current_send = null;
				var tuple = pending [0];
				pending.RemoveAt (0);
				tuple.Item1.SetResult (true);

				if (pending.Count == 1) {
					if (current_send != null)
						throw new Exception ("WTF, current_send MUST BE NULL IF THERE'S no pending send");
					current_send = Ws.SendAsync (new ArraySegment<byte> (pending[0].Item2), WebSocketMessageType.Text, true, token);
					return pending [0].Item1.Task;
				}
				return null;
			}
		}

		WsQueue GetQueueForSocket (WebSocket ws)
		{
			return queues.FirstOrDefault (q => q.Ws == ws);
		}

		WsQueue GetQueueForTask (Task task) {
			return queues.FirstOrDefault (q => q.CurrentSend == task);
		}

		Task Send (WebSocket to, JObject o, CancellationToken token)
		{
			var bytes = Encoding.UTF8.GetBytes (o.ToString ());		

			var queue = GetQueueForSocket (to);
			var task = queue.Send (bytes, token);
			pending_ops.Add (task);

			return task;
		}

		async Task OnEvent (string method, JObject args, CancellationToken token)
		{
			try {
				if (!await AcceptEvent (method, args, token)) {
					Console.WriteLine ("proxy browser: {0}::{1}",method, args);
					await SendEventInternal (method, args, token);
				}
			} catch (Exception e) {
				side_exception.TrySetException (e);
			}
		}

		async Task OnCommand (int id, string method, JObject args, CancellationToken token)
		{
			try {
				if (!await AcceptCommand (id, method, args, token)) {
					var res = await SendCommandInternal (method, args, token);
					await SendResponseInternal (id, res, token);
				}
			} catch (Exception e) {
				side_exception.TrySetException (e);
			}
		}

		void OnResponse (int id, Result result)
		{
			Console.WriteLine ("got id {0} res {1}", id, result);
			var idx = pending_cmds.FindIndex (e => e.Item1 == id);
			var item = pending_cmds [idx];
			pending_cmds.RemoveAt (idx);

			item.Item2.SetResult (result);
		}

		void ProcessBrowserMessage (string msg, CancellationToken token)
		{
			// Debug ($"browser: {msg}");
			var res = JObject.Parse (msg);

			if (res ["id"] == null)
				pending_ops.Add (OnEvent (res ["method"].Value<string> (), res ["params"] as JObject, token));
			else
				OnResponse (res ["id"].Value<int> (), Result.FromJson (res));
		}

		void ProcessIdeMessage (string msg, CancellationToken token)
		{
			var res = JObject.Parse (msg);

			pending_ops.Add (OnCommand (res ["id"].Value<int> (), res ["method"].Value<string> (), res ["params"] as JObject, token));
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
			var tcs = new TaskCompletionSource<Result> ();
			Console.WriteLine ("add cmd id {0}", id);
			pending_cmds.Add ((id, tcs));

			await Send (this.browser, o, token);
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
				queues.Add (new WsQueue (this.ide));
				using (this.browser = new ClientWebSocket ()) {
					this.browser.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
					await this.browser.ConnectAsync (browserUri, CancellationToken.None);
					queues.Add (new WsQueue (this.browser));

					Debug ("client connected");
					var x = new CancellationTokenSource ();

					pending_ops.Add (ReadOne (browser, x.Token));
					pending_ops.Add (ReadOne (ide, x.Token));
					pending_ops.Add (side_exception.Task);

					try {
						while (!x.IsCancellationRequested) {
							var task = await Task.WhenAny (pending_ops);
							if (task == pending_ops [0]) {
								var msg = ((Task<string>)task).Result;
								pending_ops [0] = ReadOne (browser, x.Token); //queue next read
								ProcessBrowserMessage (msg, x.Token);
							} else if (task == pending_ops [1]) {
								var msg = ((Task<string>)task).Result;
								pending_ops [1] = ReadOne (ide, x.Token); //queue next read
								ProcessIdeMessage (msg, x.Token);
							} else if (task == pending_ops [2]) {
								var res = ((Task<bool>)task).Result;
								throw new Exception ("side task must always complete with an exception, what's going on???");
							} else {
								//must be a background task
								pending_ops.Remove (task);
								var queue = GetQueueForTask (task);
								if (queue != null) {
									var tsk = queue.Pump (x.Token);
									if (tsk != null)
										pending_ops.Add (tsk);
								}
							}
						}
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
