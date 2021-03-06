﻿using SiweiSoft.SAPIService.Helper;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web;
using System.Web.Script.Serialization;
using System.Xml;

namespace SiweiSoft.SAPIService.Core
{
    internal class SapiRequest
    {
        //Http请求上下文
        private HttpListenerContext context;

        //请求session
        private SessionBase session;

        /// <summary>
        /// 构造方法
        /// </summary>
        /// <param name="requestContext"></param>
        /// <param name="session"></param>
        public SapiRequest(HttpListenerContext requestContext, SessionBase session)
        {
            context = requestContext;
            this.session = session;
        }

        /// <summary>
        /// 响应请求
        /// </summary>
        public void Response()
        {
            ActionResult actionResult = null;
            try
            {
                //Initialize controller instance and get action information
                ActionInfo actionInfo = null;
                ControllerBase controllerInstance = InitializeControllerInstance(out actionInfo, context.Request.RawUrl.Split('?')[0]);
                if (controllerInstance == null || actionInfo == null)
                    Log.Comment(CommentType.Error, "Controller或者对应Action未找到，可能请求的格式不正确(正确格式：{0}/ControllerName/ActionName?QueryString)。",
                        SapiService.RootPath != null ? "/" + SapiService.RootPath : null);
                else
                {
                    if (actionInfo.NeedAuthorize && (session == null || !session.IsAuthorized))
                    {
                        controllerInstance = InitializeControllerInstance(out actionInfo, "/" + SapiService.RootPath + SapiService.NotAuthorized);
                        if (controllerInstance != null && actionInfo != null)
                            actionResult = (ActionResult)actionInfo.Action.Invoke(controllerInstance, null);
                        else
                            actionResult = new ActionNotAuthorized();
                    }
                    else
                    {
                        controllerInstance.Session = this.session;
                        controllerInstance.Parameters = GetRequestParameters();
                        controllerInstance.ServerConfigs = SapiService.ServerConfigs;

                        actionResult = controllerInstance.PreResponse();
                        actionResult = actionResult ?? (ActionResult)actionInfo.Action.Invoke(controllerInstance, null);
                        controllerInstance.PostResponse();
                    }
                }

                //Response
                if (actionResult == null)
                {
                    context.Response.StatusCode = 404;
                }
                else
                {
                    //添加请求的头部
                    if (actionResult.Headers != null)
                    {
                        foreach (string head in actionResult.Headers)
                        {
                            context.Response.Headers.Add(head);
                        }
                    }

                    //下载文件请求
                    if (actionResult.Stream != null)
                    {
                        int receivedLength = 0;
                        byte[] buffer = new byte[10240];
                        do
                        {
                            receivedLength = actionResult.Stream.Read(buffer, 0, buffer.Length);
                            context.Response.OutputStream.Write(buffer, 0, receivedLength);
                        }
                        while (receivedLength > 0);
                        actionResult.Stream.Close();
                        context.Response.StatusCode = 200;
                    }
                    else  //数据请求
                    {
                        byte[] buffer = Encoding.UTF8.GetBytes(actionResult.GetResultString());
                        context.Response.ContentLength64 = buffer.Length;
                        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                        context.Response.StatusCode = 200;
                    }

                }
            }
            catch (Exception ex)
            {
                //关闭文件流
                if (actionResult != null && actionResult.Stream != null)
                    actionResult.Stream.Close();

                context.Response.StatusCode = 500;
                Log.Comment(CommentType.Error, "未知错误：" + ex.Message);
            }
            finally
            {
                context.Response.Close();
            }
        }

        private Dictionary<string, object> GetRequestParameters()
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>();

            string requestMethod = context.Request.HttpMethod.ToUpper();

            var urlParts = context.Request.RawUrl.Split('?');
            if (urlParts.Length > 1)
                GetURLParameters(ref parameters, urlParts[1]);

            if (requestMethod == "POST" && context.Request.InputStream.CanRead)
            {
                string contentType = context.Request.Headers["Content-Type"];
                if (contentType != null && contentType.Contains("multipart/form-data"))
                {
                    HttpFile uploadFile = HttpFileUtilities.SaveFile(context.Request, SapiService.FileServerPath, Encoding.UTF8);
                    parameters.Add("UploadFile", uploadFile);
                }
                else
                {
                    string postData = null;

                    using (Stream inputStream = context.Request.InputStream)
                    {
                        byte[] buffer = new byte[8192];
                        int length = 0;
                        do
                        {
                            length = inputStream.Read(buffer, 0, buffer.Length);
                            postData += Encoding.UTF8.GetString(buffer, 0, length);
                        }
                        while (length > 0);
                    }

                    if (postData.StartsWith("{"))
                    {
                        Dictionary<string, object> param = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(postData);
                        foreach (var p in param)
                        {
                            parameters.Add(p.Key, p.Value);
                        }
                    }
                    else if (postData.StartsWith("<xml>"))
                    {
                        XmlDocument doc = new XmlDocument();
                        doc.LoadXml(postData);
                        XmlNode toUserName = doc.SelectSingleNode("/xml/ToUserName");
                        XmlNode fromUserName = doc.SelectSingleNode("/xml/FromUserName");
                        XmlNode content = doc.SelectSingleNode("/xml/Content");
                        XmlNode msgType = doc.SelectSingleNode("/xml/MsgType");
                        XmlNode _event = doc.SelectSingleNode("/xml/Event");
                        parameters.Add("ToUserName", toUserName.InnerText);
                        parameters.Add("FromUserName", fromUserName.InnerText);
                        parameters.Add("MsgType", msgType.InnerText);
                        parameters.Add("Content", content != null ? content.InnerText : null);
                        parameters.Add("Event", _event != null ? _event.InnerText : null);
                    }
                    else
                        GetURLParameters(ref parameters, postData);
                }
            }
            return parameters;
        }

        private void GetURLParameters(ref Dictionary<string, object> parameters, string queryString)
        {
            queryString = HttpUtility.UrlDecode(queryString);
            if (!String.IsNullOrEmpty(queryString))
            {
                string[] paramsPart = queryString.Trim('&').Split('&');
                foreach (string paramPart in paramsPart)
                {
                    string[] keyValue = paramPart.Split('=');
                    if (keyValue.Length == 2)
                        parameters.Add(keyValue[0], keyValue[1]);
                }
            }
        }

        /// <summary>
        /// 初始化Controller实例
        /// </summary>
        /// <param name="actionInfo"></param>
        /// <param name="url"></param>
        /// <returns></returns>
        private ControllerBase InitializeControllerInstance(out ActionInfo actionInfo, string url)
        {
            ControllerBase controller = null;
            actionInfo = null;

            //Get root name, controller name, and action name from request
            string[] urlParts = url.Split('/');
            if ((urlParts.Length == 4 && String.Compare(urlParts[1], SapiService.RootPath, true) == 0)
                || urlParts.Length == 3)
            {
                string controllerName = urlParts[2].ToUpper();
                string actionName = urlParts[3].ToUpper();

                ControllerReflectionInfo controllerInfo = SapiService.ControllersInfos.ContainsKey(controllerName) ? SapiService.ControllersInfos[controllerName] : null;
                if (controllerInfo != null)
                {
                    actionInfo = controllerInfo != null ? controllerInfo.GetMethodInfoByAlias(actionName) : null;
                    controller = ((ControllerBase)controllerInfo.ControllerInstance).Clone();
                }
            }
            else
                Log.Comment(CommentType.Error, "请求URL格式不正确（正确格式：{0}/ControllerName/ActionName）。",
                    SapiService.RootPath != null ? "/" + SapiService.RootPath : null);
            return controller;
        }
    }
}
