/*<FILE_LICENSE>
* NFX (.NET Framework Extension) Unistack Library
* Copyright 2003-2014 IT Adapter Inc / 2015 Aum Code LLC
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
</FILE_LICENSE>*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using NFX.Environment;
using NFX.Financial;
using NFX.Instrumentation;
using NFX.Log;
using NFX.Serialization.JSON;

namespace NFX.Web.EMail
{
    /// <summary>
    /// Implements mailer sink using Mailgun service
    /// </summary>
    public sealed class MailgunMailerSink:MailerSink, IWebClientCaller, IInstrumentable
    {
        #region Consts

        private static readonly TimeSpan INSTR_INTERVAL = TimeSpan.FromSeconds(10);

        //Mail standard parameters
        private const string BASE_SERVICE_URL = "https://api.mailgun.net/v3";
        private const string MAIL_PARAM_FROM = "from";
        private const string MAIL_PARAM_TO = "to";
        private const string MAIL_PARAM_CC = "cc";
        private const string MAIL_PARAM_BCC = "bcc";
        private const string MAIL_PARAM_SUBJECT = "subject";
        private const string MAIL_PARAM_TEXT = "text";
        private const string MAIL_PARAM_HTML = "html";
        private const string MAIL_PARAM_ATTACHMENT = "attachment";
        private const string MAIL_PARAM_INLINE = "inline";

        // Mailgun specific parameters. Reffer to https://documentation.mailgun.com/api-sending.html#sending
        private const string API_PARAM_TAG = "o:tag";
        private const string API_PARAM_CAMPAIGN = "o:campaign";
        private const string API_PARAM_DKIM_ENABLED = "o:dkim";
        private const string API_PARAM_DELIVERYTIME = "o:deliverytime";
        private const string API_PARAM_TESTMODE = "o:testmode";
        private const string API_PARAM_TRACKING = "o:tracking";
        private const string API_PARAM_TRACKING_CLICKS = "o:tracking-clicks";
        private const string API_PARAM_TRACKING_OPENS = "o:tracking-opens";

        #endregion

        #region .ctor

        public MailgunMailerSink(MailerService director) : base(director)
        {
        }

        #endregion

        #region Private Fields

        private int m_WebServiceCallTimeoutMs;
        private long m_stat_EmailsCount, m_stat_EmailsErrorCount;
        private bool m_InstrumentationEnabled;
        private Time.Event m_InstrumentationEvent;

        #endregion

        #region Properties

        [Config]
        public string AuthorizationKey { get; set; }

        [Config]
        public string Domain { get; set; }

        [Config]
        public string DefaultFromAddress { get; set; }

        [Config]
        public string DefaultFromName { get; set; }

        [Config]
        public bool TestMode { get; set; }

     
        #endregion

        #region IWebClientCaller

        [Config(Default = 20000)]
        public int WebServiceCallTimeoutMs
        {
            get { return m_WebServiceCallTimeoutMs; }
            set { m_WebServiceCallTimeoutMs = value < 0 ? 0 : value; }
        }

        [Config(Default = false)]
        public bool KeepAlive { get; set; }

        [Config(Default = false)]
        public bool Pipelined { get; set; }

        public string ServiceUrl
        {
            get
            {
                return "{0}/{1}/messages".Args(BASE_SERVICE_URL, Domain);
            }
        }

        public dynamic Result { get; set; }

        #endregion

        #region IInstrumentable

        /// <summary>
        /// Implements IInstrumentable
        /// </summary>
        [Config(Default = false)]
        [ExternalParameter(CoreConsts.EXT_PARAM_GROUP_INSTRUMENTATION, CoreConsts.EXT_PARAM_GROUP_WEB)]
        public override bool InstrumentationEnabled
        {
            get { return m_InstrumentationEnabled; }
            set
            {
                m_InstrumentationEnabled = value;
                if (m_InstrumentationEvent == null)
                {
                    if (!value) return;
                    ResetStats();
                    m_InstrumentationEvent = new Time.Event(App.EventTimer, null, e => AcceptManagerVisit(this, e.LocalizedTime), INSTR_INTERVAL);
                }
                else
                {
                    if (value) return;
                    DisposableObject.DisposeAndNull(ref m_InstrumentationEvent);
                }
            }
        }

        #endregion

        #region MailerSink

        protected override void DoSendMsg(MailMsg msg)
        {
            if (msg == null || msg.TOAddress.IsNullOrWhiteSpace()) return;

            var request = new WebClient.RequestParams()
            {
                Caller = this,
                Method = HTTPRequestMethod.POST,
                Uri = new Uri(ServiceUrl),
                Headers = new Dictionary<string, string>(),
                BodyParameters = new Dictionary<string,string>(),
                UName = "api",
                UPwd = AuthorizationKey
            };

            var fromAddress = "{0} <{1}>".Args(DefaultFromName, DefaultFromAddress);
            if (msg.FROMAddress.IsNotNullOrWhiteSpace())
            {
                fromAddress = "{0} <{1}>".Args(msg.FROMName, msg.FROMAddress);
            }

            AddParameter(request.BodyParameters,MAIL_PARAM_FROM, fromAddress);
            AddParameter(request.BodyParameters,MAIL_PARAM_TO, "{0} <{1}>".Args(msg.TOName, msg.TOAddress));
            AddParameter(request.BodyParameters,MAIL_PARAM_CC, msg.CC);
            AddParameter(request.BodyParameters,MAIL_PARAM_BCC, msg.BCC);
            AddParameter(request.BodyParameters,MAIL_PARAM_SUBJECT, msg.Subject);
            AddParameter(request.BodyParameters,MAIL_PARAM_TEXT, msg.Body);
            AddParameter(request.BodyParameters,MAIL_PARAM_HTML, msg.HTMLBody);

            if (TestMode)
            {
                request.BodyParameters.Add(API_PARAM_TESTMODE, "Yes");
            }
            try
            {
                Result = WebClient.GetJsonAsDynamic(request);
                if ((Result.id as string).IsNotNullOrWhiteSpace())
                {
                    StatSend();
                    App.Log.Write(new Message
                    {
                        Type = MessageType.Debug,
                        From = this.GetType().FullName,
                        Text = string.Format("Mail sending result: id {0}, message {1}", Result.id, Result.message)
                    });
                }
                else
                {
                    App.Log.Write(new Message
                    {
                        Type = MessageType.Error,
                        From = this.GetType().FullName,
                        Text = string.Format("Mail sending failed:{0}", Result.message)
                    });
                    StatSendError();
                }
            }
            catch (System.Net.WebException wex)
            {
                if (wex.Response != null)
                {
                    using (var errorResponse = (HttpWebResponse)wex.Response)
                    {
                        using (var reader = new StreamReader(errorResponse.GetResponseStream()))
                        {
                            string error = reader.ReadToEnd();
                            var jsonError = error.IsNotNullOrWhiteSpace() ? error.JSONToDynamic() : null;
                            App.Log.Write(new Message
                            {
                                Type = MessageType.Error,
                                From = this.GetType().FullName,
                                Text = string.Format("Mail sending failed:{0}", jsonError ==null? "Unknown error":jsonError.message)
                            });

                        }
                    }
                }
                StatSendError();
                throw;
            }
            catch (Exception e)
            {
                StatSendError();
                throw;
            }
           
        }
        #endregion

        protected override void DoAcceptManagerVisit(object manager, DateTime managerNow)
        {
            DumpStats();
        }

        #region .pvt. impl.
        private void AddParameter(IDictionary<string, string> parameters, string name, string value)
        {
            if (parameters == null) return;
            if (name.IsNullOrWhiteSpace() || value.IsNullOrWhiteSpace()) return;
            
            parameters.Add(name,Uri.EscapeDataString(value));
        }

        private void DumpStats()
        {
            var src = this.Name;
            Instrumentation.MailSinkCount.Record(src, m_stat_EmailsCount);
            m_stat_EmailsCount = 0;

            Instrumentation.MailSinkErrorCount.Record(src, m_stat_EmailsErrorCount);
            m_stat_EmailsErrorCount = 0;
        }

        private void ResetStats()
        {
            m_stat_EmailsCount = 0;
            m_stat_EmailsErrorCount = 0;
        }
        private void StatSendError()
        {
            Interlocked.Increment(ref m_stat_EmailsErrorCount);
        }

        private void StatSend()
        {
            Interlocked.Increment(ref m_stat_EmailsCount);
        }
        #endregion
    }
}
