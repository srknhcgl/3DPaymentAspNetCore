using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;
using ThreeDPayment.Requests;
using ThreeDPayment.Results;

namespace ThreeDPayment.Providers
{
    public class PosnetPaymentProvider : IPaymentProvider
    {
        private readonly HttpClient client;

        public PosnetPaymentProvider(IHttpClientFactory httpClientFactory)
        {
            client = httpClientFactory.CreateClient();
        }

        public async Task<PaymentGatewayResult> ThreeDGatewayRequest(PaymentGatewayRequest request)
        {
            string merchantId = request.BankParameters["merchantId"];
            string terminalId = request.BankParameters["terminalId"];
            string posnetId = request.BankParameters["posnetId"];

            try
            {
                //yapıkredi bankasında tutar bilgisinde nokta, virgül gibi değerler istenmiyor. 1.10 TL'lik işlem 110 olarak gönderilmeli. Yani tutarı 100 ile çarpabiliriz.
                string amount = (request.TotalAmount * 100m).ToString("0.##", new CultureInfo("en-US"));//virgülden sonraki sıfırlara gerek yok

                string requestXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                                        <posnetRequest>
                                            <mid>{merchantId}</mid>
                                            <tid>{terminalId}</tid>
                                            <oosRequestData>
                                                <posnetid>{posnetId}</posnetid>
                                                <XID>{request.OrderNumber}</XID>
                                                <amount>{amount}</amount>
                                                <currencyCode>{CurrencyCodes[request.CurrencyIsoCode]}</currencyCode>
                                                <installment>{string.Format("{0:00}", request.Installment)}</installment>
                                                <tranType>Sale</tranType>
                                                <cardHolderName>{request.CardHolderName}</cardHolderName>
                                                <ccno>{request.CardNumber}</ccno>
                                                <expDate>{request.ExpireMonth}{request.ExpireYear}</expDate>
                                                <cvc>{request.CvvCode}</cvc>
                                            </oosRequestData>
                                        </posnetRequest>";

                Dictionary<string, string> httpParameters = new Dictionary<string, string>();
                httpParameters.Add("xmldata", requestXml);

                HttpResponseMessage response = await client.PostAsync(request.BankParameters["verifyUrl"], new FormUrlEncodedContent(httpParameters));
                string responseContent = await response.Content.ReadAsStringAsync();

                XmlDocument xmlDocument = new XmlDocument();
                xmlDocument.LoadXml(responseContent);

                if (xmlDocument.SelectSingleNode("posnetResponse/approved") == null ||
                    xmlDocument.SelectSingleNode("posnetResponse/approved").InnerText != "1")
                {
                    string errorMessage = xmlDocument.SelectSingleNode("posnetResponse/respText")?.InnerText ?? string.Empty;
                    if (string.IsNullOrEmpty(errorMessage))
                    {
                        errorMessage = "Bankadan hata mesajı alınamadı.";
                    }

                    return PaymentGatewayResult.Failed(errorMessage);
                }

                XmlNode data1Node = xmlDocument.SelectSingleNode("posnetResponse/oosRequestDataResponse/data1");
                XmlNode data2Node = xmlDocument.SelectSingleNode("posnetResponse/oosRequestDataResponse/data2");
                XmlNode signNode = xmlDocument.SelectSingleNode("posnetResponse/oosRequestDataResponse/sign");

                Dictionary<string, object> parameters = new Dictionary<string, object>();
                parameters.Add("posnetData", data1Node.InnerText);
                parameters.Add("posnetData2", data2Node.InnerText);
                parameters.Add("digest", signNode.InnerText);

                parameters.Add("mid", merchantId);
                parameters.Add("posnetID", posnetId);

                //Vade Farklı işlemler için kullanılacak olan kampanya kodunu belirler.
                //Üye İşyeri için tanımlı olan kampanya kodu, İşyeri Yönetici Ekranlarına giriş yapıldıktan sonra, Üye İşyeri bilgileri sayfasından öğrenilebilinir.
                parameters.Add("vftCode", string.Empty);

                parameters.Add("merchantReturnURL", request.CallbackUrl);//geri dönüş adresi
                parameters.Add("lang", request.LanguageIsoCode);
                parameters.Add("url", string.Empty);//openANewWindow 1 olarak ayarlanırsa buraya gidilecek url verilmeli
                parameters.Add("openANewWindow", "0");//POST edilecek formun yeni bir sayfaya mı yoksa mevcut sayfayı mı yönlendirileceği
                parameters.Add("useJokerVadaa", "1");//yapıkredi kartlarında vadaa kullanılabilirse izin verir

                return PaymentGatewayResult.Successed(parameters, request.BankParameters["gatewayUrl"]);
            }
            catch (Exception ex)
            {
                return PaymentGatewayResult.Failed(ex.ToString());
            }
        }

        public async Task<VerifyGatewayResult> VerifyGateway(VerifyGatewayRequest request, PaymentGatewayRequest gatewayRequest, IFormCollection form)
        {
            if (form == null)
            {
                return VerifyGatewayResult.Failed("Form verisi alınamadı.");
            }

            if (!form.ContainsKey("BankPacket") || !form.ContainsKey("MerchantPacket") || !form.ContainsKey("Sign"))
            {
                return VerifyGatewayResult.Failed("Form verisi alınamadı.");
            }

            string merchantId = request.BankParameters["merchantId"];
            string terminalId = request.BankParameters["terminalId"];

            string requestXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                                    <posnetRequest>
                                        <mid>{merchantId}</mid>
                                        <tid>{terminalId}</tid>
                                        <oosResolveMerchantData>
                                            <bankData>{form["BankPacket"]}</bankData>
                                            <merchantData>{form["MerchantPacket"]}</merchantData>
                                            <sign>{form["Sign"]}</sign>
                                        </oosResolveMerchantData>
                                    </posnetRequest>";

            Dictionary<string, string> httpParameters = new Dictionary<string, string>();
            httpParameters.Add("xmldata", requestXml);

            HttpResponseMessage response = await client.PostAsync(request.BankParameters["verifyUrl"], new FormUrlEncodedContent(httpParameters));
            string responseContent = await response.Content.ReadAsStringAsync();

            XmlDocument xmlDocument = new XmlDocument();
            xmlDocument.LoadXml(responseContent);

            if (xmlDocument.SelectSingleNode("posnetResponse/approved") == null ||
                xmlDocument.SelectSingleNode("posnetResponse/approved").InnerText != "1" ||
                xmlDocument.SelectSingleNode("posnetResponse/approved").InnerText != "2")
            {
                string errorMessage = "3D doğrulama başarısız.";
                if (xmlDocument.SelectSingleNode("posnetResponse/respText") != null)
                {
                    errorMessage = xmlDocument.SelectSingleNode("posnetResponse/respText").InnerText;
                }

                return VerifyGatewayResult.Failed(errorMessage, form["ApprovedCode"],
                    xmlDocument.SelectSingleNode("posnetResponse/approved").InnerText);
            }

            int.TryParse(form["InstalmentNumber"], out int instalmentNumber);

            return VerifyGatewayResult.Successed(form["HostLogKey"], $"{form["HostLogKey"]}-{form["AuthCode"]}",
                instalmentNumber, 0,
                xmlDocument.SelectSingleNode("posnetResponse/respText")?.InnerText,
                form["ApprovedCode"]);
        }

        public Dictionary<string, string> TestParameters => new Dictionary<string, string>
        {
            { "merchantId", "" },
            { "terminalId", "" },
            { "posnetId", "" },
            { "verifyUrl", "https://posnettest.yapikredi.com.tr/PosnetWebService/XML" },
            { "gatewayUrl", "https://posnettest.yapikredi.com.tr/PosnetWebService/XML" }
        };

        private static readonly IDictionary<string, string> CurrencyCodes = new Dictionary<string, string>
        {
            { "949", "TL" },
            { "840", "USD" },
            { "978", "EUR" },
            { "826", "GBP" }
        };
    }
}