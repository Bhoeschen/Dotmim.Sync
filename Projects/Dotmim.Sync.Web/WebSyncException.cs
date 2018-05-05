using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;

namespace Dotmim.Sync.Web
{
	public class WebSyncException : Exception
	{
		public WebSyncException(string message) : base(message)
		{
		}

		public WebSyncException(SerializationInfo info, StreamingContext context) : base(info, context)
		{

		}

		public String Type { get; set; }
		/// <summary>
		/// Get message string from Exception
		/// </summary>
		internal static WebSyncException GetWebSyncException(Exception exception)
		{
			WebSyncException webSyncException = new WebSyncException(exception.Message);

			webSyncException.Type = exception.GetType().ToString();
		   
			return webSyncException;
		}
	}
}
