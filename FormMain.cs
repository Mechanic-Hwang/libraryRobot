using System;
using System.Data;
using System.Data.SqlClient;
using System.Net.NetworkInformation;
using System.Windows.Forms;
using Core = Invengo.NetAPI.Core;
using IRP1 = Invengo.NetAPI.Protocol.IRP1;
using System.Threading;
using System.IO;
using System.IO.Ports;
using MySql.Data.MySqlClient;
using System.Net.Http;
using Newtonsoft.Json;
using System.Text;
using System.Net;
using System.Collections.Generic;
using Newtonsoft;
using Newtonsoft.Json.Linq;

namespace SingleReaderTest
{
    public partial class FormMain : Form
    {
        // 实例化读写器类
        SerialPort port;

        //desktop
        IRP1.Reader reader;//串口

        //IRP1.Reader reader = new IRP1.Reader("Reader1", "TCPIP_Client", "192.168.1.230:7086");//网口
        IRP1.ReadTag scanMsg = new IRP1.ReadTag(IRP1.ReadTag.ReadMemoryBank.EPC_6C);//扫描消息
        DataTable myDt = new DataTable();//显示扫描数据
        object lockobj = new object();//显示数据锁定
        bool isTryReconnNet = false;
        int tryReconnNetTimeSpan;
        //Database connection
        MySqlConnection mycon = new MySqlConnection("Server=172.16.191.135;User Id=root;password=admin;Database=library");
        //timer control
        System.Timers.Timer timer = null;
        long timeCount = 0;

        public FormMain()
        {
            try
            {
                mycon.Open();
            }
            catch(Exception ex)
            {
                MessageBox.Show("You should link to the database.");
            }
            
            InitializeComponent();

            //traversing all possible serial ports, use the first one
            string[] serialPort = SerialPort.GetPortNames();

            //add the available ports to selectButton
            foreach (var port in serialPort)
            {
                comboBox1.Items.Add(port);
                
            }
            
            reader = new IRP1.Reader("Reader1", "RS232", serialPort[0] + ",115200");

            this.FormClosed += new FormClosedEventHandler(FormMain_FormClosed);

            if (port == null)
            {
                //COM4为Arduino使用的串口号，需根据实际情况调整
                //port = new SerialPort("COM4", 9600);
                //port.Open();
            }

            myDt.Columns.Add("EPC");
            myDt.Columns.Add("barcode");
            myDt.Columns.Add("Count");
            dataGridView1.DataSource = myDt;
            dataGridView1.Columns[0].HeaderText = "EPC";
            dataGridView1.Columns[1].HeaderText = "Barcode";
            dataGridView1.Columns[2].HeaderText = "Count";

            IRP1.Reader.OnApiException += new Core.ApiExceptionHandle(Reader_OnApiException);
        }

        void FormMain_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (port != null && port.IsOpen)
            {
                port.Close();
            }
        }


        void Reader_OnApiException(Core.ErrInfo e)
        {
            if (e.Ei.ErrCode == "FF22")
            {
                changeCtrlEnable("disconn");
                showMsg(e.Ei.ErrMsg);
                if (isTryReconnNet)
                    ReConn();
            }
            else if (e.Ei.ErrCode == "FF24")//发现连接作废,不作断网恢复尝试
            {
                isTryReconnNet = false;
                changeCtrlEnable("disconn");
                showMsg(e.Ei.ErrMsg);
            }
        }

        #region 重连接
        private void ReConn()
        {
            bool isSuc = false;
            using (Ping ping = new Ping())
            {
                for (int i = 0; i < tryReconnNetTimeSpan * 60; i++)
                {
                    PingReply pingReply = null;
                    try
                    {
                        pingReply = ping.Send(reader.ConnString.Substring(0,
                            reader.ConnString.IndexOf(':')), 1000);//超时为1秒

                        if (pingReply.Status != IPStatus.Success)
                        {
                            showMsg("Ping 不通");
                            continue;
                        }
                        else
                        {
                            isSuc = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        showMsg("尝试自动恢复连接失败！" + ex.Message);
                        return;
                    }
                }
            }
            //建立连接
            if (isSuc)
            {
                isSuc = false;
                for (int i = 0; i < 3; i++)//尝试3次
                {
                    if (reader.Connect())
                    {
                        showMsg("尝试自动恢复连接成功！");
                        changeCtrlEnable("conn");
                        isSuc = true;
                        break;
                    }
                    else
                    {
                        Thread.Sleep(2000);
                        continue;
                    }
                }
                if (!isSuc)
                    showMsg("尝试自动恢复连接失败！");
            }
        }
        #endregion

        // 关闭窗体
        private void FormMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            btnStop_Click(sender, e);
            mycon.Close();
            Environment.Exit(Environment.ExitCode);
        }

        // 建立连接
        private void btnConn_Click(object sender, EventArgs e)
        {
            if (reader.Connect())
            {
                changeCtrlEnable("conn");
                //注册接收读写器消息事件
                reader.OnMessageNotificationReceived += new Invengo.NetAPI.Core.MessageNotificationReceivedHandle(reader_OnMessageNotificationReceived);
                lblMsg.Text = "Connection Successful!";
            }
            else
            {
                reader = new IRP1.Reader("Reader1", "RS232", this.comboBox1.Text + ",115200");
                if (reader.Connect())
                {
                    changeCtrlEnable("conn");
                    //注册接收读写器消息事件
                    reader.OnMessageNotificationReceived += new Invengo.NetAPI.Core.MessageNotificationReceivedHandle(reader_OnMessageNotificationReceived);
                    lblMsg.Text = "Connection Successful!";

                }
                else
                {
                    lblMsg.Text = "Connection Failed!";
                    MessageBox.Show("Failed to create the connection.");
                }
            }
        }

        /// <summary>
        /// 接收到读写器消息触发事件
        /// </summary>
        /// <param name="reader">读写器类</param>
        /// <param name="msg">消息内容</param>
        void reader_OnMessageNotificationReceived(Invengo.NetAPI.Core.BaseReader reader, Invengo.NetAPI.Core.IMessageNotification msg)
        {
            if (msg.StatusCode != 0)
            {
                //显示错误信息
                showMsg(msg.ErrInfo);
                return;
            }
            String msgType = msg.GetMessageType();
            msgType = msgType.Substring(msgType.LastIndexOf('.') + 1);
            switch (msgType)
            {
                #region RXD_TagData
                case "RXD_TagData":
                    {
                        IRP1.RXD_TagData m = (IRP1.RXD_TagData)msg;
                        string tagType = m.ReceivedMessage.TagType;
                        display(m);
                    }
                    break;
                #endregion               
                #region RXD_IOTriggerSignal_800
                case "RXD_IOTriggerSignal_800":
                    {
                        IRP1.RXD_IOTriggerSignal_800 m = (IRP1.RXD_IOTriggerSignal_800)msg;
                        if (m.ReceivedMessage.IsStart)
                        {
                            changeCtrlEnable("scan");
                            showMsg(" I/O 触发，正在读卡...");
                        }
                        else
                        {
                            changeCtrlEnable("conn");
                            showMsg(" I/O 触发，停止读卡");
                        }
                    }
                    break;
                    #endregion
            }
        }

        #region 状态栏显示信息
        private delegate void showMsgHandle(string str);

        private void showMsg(string str)
        {
            if (this.InvokeRequired)
            {
                showMsgHandle h = new showMsgHandle(showMsgMethod);
                this.BeginInvoke(h, str);
            }
            else
            {
                showMsgMethod(str);
            }
        }

        private void showMsgMethod(string str)
        {
            lblMsg.Text = str;
        }
        #endregion

        #region 显示扫描标签数据
        private delegate void displayHandle(IRP1.RXD_TagData msg);

        private void display(IRP1.RXD_TagData msg)
        {
            if (dataGridView1.InvokeRequired)
            {
                displayHandle h = new displayHandle(displayMethod);
                dataGridView1.BeginInvoke(h, msg);
            }
            else
            {
                displayMethod(msg);
            }
        }

        //Display what scan on the windows
        private void displayMethod(IRP1.RXD_TagData msg)
        {
            lock (lockobj)
            {
                bool isAdd = true;
                int count = 0;
                string barcode, layercode;
                string epc = Core.Util.ConvertByteArrayToHexString(msg.ReceivedMessage.EPC);
                string tid = Core.Util.ConvertByteArrayToHexString(msg.ReceivedMessage.TID);
                foreach (DataRow dr in myDt.Rows)
                {
                    //whether is showed on the windows
                    if ((dr["EPC"] != null && dr["EPC"].ToString() != "" && dr["EPC"].ToString() == epc))
                    {
                        isAdd = false;
                        count = int.Parse(dr["Count"].ToString()) + 1;
                        dr["Count"] = count;
                    }
                }
                
                if (isAdd)
                {
                    DataRow mydr = myDt.NewRow();
                    mydr["EPC"] = epc;
                    mydr["Count"] = 1;
                    myDt.Rows.Add(mydr);
                    //add data to database
                    if (epc[9] == '2')//2 stands for layer code and should be stored into a different database
                    {
                        layercode = transLayerCode(epc);
                        mydr["barcode"] = "Layer: " + layercode;
                    }
                    else if (epc[9] == '0')//0 stands for book code
                    {
                        barcode = convertBookLayer(epc);
                        mydr["barcode"] = barcode;
                    }
                }
            }
        }
        #endregion

        #region class types of json response
        public class Book
        {
            public string barcode { get; set; }
            public string title { get; set; }
            public string callNo { get; set; }
            public string isbn { get; set; }
        }

        public class Data
        {
            public List<Book> book { get; set; }
        }

        public class RootObject
        {
            public string code { get; set; }
            public string message { get; set; }
            public Data data { get; set; }
        }
        #endregion
        //transform epc of layer code to get info
        private string transLayerCode(string epc)
        {
            string temp;
            string layerCode = "01";//the first two numbers always is '01'

            temp = epc.Substring(2, 4);//get 4 numbers from the third number of String 
            int libCode = Convert.ToInt32(temp, 16);
            //the library code does not need to be added onto the layer code
            temp = epc.Substring(10, 2);//level code
            int level = Convert.ToInt32(temp, 16);
            level = level / 8;
            layerCode += level.ToString().PadLeft(2, '0');
            temp = epc.Substring(12, 1);//room code
            int room = Convert.ToInt32(temp, 16);
            room = room / 2;
            layerCode += room.ToString().PadLeft(2, '0');
            temp = epc.Substring(13, 3);//shelf code
            int shelf = Convert.ToInt32(temp, 16);
            shelf = shelf / 8;
            layerCode += shelf.ToString().PadLeft(3, '0');
            temp = epc.Substring(16, 2);//column code
            int column = Convert.ToInt32(temp, 16);
            column = column / 2;
            layerCode += column.ToString().PadLeft(3, '0');
            temp = epc.Substring(18, 1);//tier code
            int tier = Convert.ToInt32(temp, 16);
            tier = tier / 2;
            layerCode += tier.ToString().PadLeft(2, '0');
            fetchLayerInfo(libCode, level, room, shelf, column, tier, layerCode);
            return layerCode;
        }

        //acquire books info by one layercode send back from the server
        private void fetchLayerInfo(int libCode, int level, int room, int shelf, int column, int tier, string layerCode)
        {
            MySqlDataAdapter mysda = new MySqlDataAdapter("SELECT LayerCode FROM `layer` ", mycon);
            DataTable dt = new DataTable();
            string result = " ";
            bool flag = false;

            mysda.Fill(dt);
            foreach (DataRow layer in dt.Rows)//if the layer code has already been stored into database, skip the process
            {
                result = layer["LayerCode"].ToString();
                if (result.Equals(layerCode))
                {
                    flag = true;
                }
                else
                {
                    flag = false;
                }
            }

            if (flag == false)
            {
                try
                {
                    //store layer info in database
                    MySqlCommand store = new MySqlCommand("INSERT INTO layer (LibraryCode, Level, RoomNumber, Shelf, ColumnNumber, Tier, LayerCode) VALUES ('"
                        + libCode + "','" + level + "','" + room + "','" + shelf + "','" + column + "','" + tier + "','" + layerCode + "')", mycon);
                    store.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    //MessageBox.Show(ex.Message);
                    flag = true;
                }
            }

            if (flag == false)
            {
                try
                {
                    //the link is provided by UIC Library staffs
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://lrctest.uic.edu.hk/Robot/api/getBarcode");
                    //using json application with 'POST' way
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    //keyword
                    //byte[] data = Encoding.UTF8.GetBytes("{\"number\": \"01010100200401\"}");
                    byte[] data = Encoding.UTF8.GetBytes("{\"number\": \" " + layerCode + "\"}");
                    //byte[] data = Encoding.UTF8.GetBytes("{\"number\": \"01020201900202\"}");
                    
                    req.ContentLength = data.Length;
                    using (Stream reqStream = req.GetRequestStream())
                    {
                        reqStream.Write(data, 0, data.Length);
                        reqStream.Close();
                    }
                    System.Net.HttpWebResponse resp = (HttpWebResponse)req.GetResponse();
                    Stream stream = resp.GetResponseStream();
                    //acquire results from UIC library server
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        result = reader.ReadToEnd();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }
                try
                {
                    //Transform JObject to JArray
                    JObject @object = (JObject)JsonConvert.DeserializeObject(result);
                    JArray dataBack = (JArray)@object["data"]["book"];
                    MessageBox.Show(dataBack.ToString());
                    for (int i = 0; i < dataBack.Count; i++)
                    {
                        JObject item = (JObject)dataBack[i];
                        string barcode = (string)item["barcode"];
                        string title = (string)item["title"];
                        string callNo = (string)item["callNo"];
                        string isbn = (string)item["isbn"];

                        //store in database
                        InsertLayerInfo(barcode, title, callNo, isbn);
                    }
                }
                catch (Exception ex){
                    MessageBox.Show(ex.Message);
                }
                
            }
        }
        private void InsertLayerInfo(string barcode, string title, string callNo, string isbn)
        {
            try
            { 
                //insert to database
                MySqlCommand storeLayerInfo = new MySqlCommand("INSERT INTO `book` (barcode, title, callNo, isbn) VALUES ('" + barcode + "',\" " + title + "\",'" + callNo + "','" + isbn + "')", mycon);
                storeLayerInfo.ExecuteNonQuery();
            }
            catch (Exception ee)
            {
                MessageBox.Show(ee.Message);
            }
        }

        //acquire the layer number that one book belongs to by the server
        private string convertBookLayer(string epc)
        {
            long code;
            string barcode = "A";//barcode start from the letter 'A'
            string temp = epc.Substring(10, 10);
            try
            {
                code = Convert.ToInt64(temp, 16);
                temp = code.ToString().Substring(2, 9);
                barcode += temp;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
            fetchBarcodeLayercode(barcode);
            return barcode;
        }

        //send to server to get the layer info by one barcode from the book
        private void fetchBarcodeLayercode(string barcode)
        {
            string result = "";
            try
            {
                //the link is provided by UIC Library staffs
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://lrctest.uic.edu.hk/Robot/api/getLayerCode");
                //using json application with 'POST' way
                req.Method = "POST";
                req.ContentType = "application/json";
                //keyword
                byte[] data = Encoding.UTF8.GetBytes("{\"barcode\":\"" + barcode + "\"}");
                req.ContentLength = data.Length;
                using (Stream reqStream = req.GetRequestStream())
                {
                    reqStream.Write(data, 0, data.Length);
                    reqStream.Close();
                }
                System.Net.HttpWebResponse resp = (HttpWebResponse)req.GetResponse();
                Stream stream = resp.GetResponseStream();
                //acquire results from UIC library server
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    result = reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }

            //Transform JObject to JArray
            JObject @object = (JObject)JsonConvert.DeserializeObject(result);
            JArray dataBack = (JArray)@object["data"]["code"];
            for (int i = 0; i < dataBack.Count; i++)
            {
                JObject item = (JObject)dataBack[i];
                string layercode = (string)item["number"];
                //store in database
                InsertBookInfo(barcode, layercode);
            }
        }

        //store both book's barcode and layercode
        private void InsertBookInfo(string barcode, String layercode)
        {
            try
            {
                //insert to database command
                MySqlCommand storeLayerInfo = new MySqlCommand("INSERT INTO bookread (barCode, layerCode) VALUES ('"
                        + barcode + "','" + layercode + "')", mycon);
                storeLayerInfo.ExecuteNonQuery();
            }
            catch (Exception ee)
            {
                //MessageBox.Show(ee.Message);
            }
        }

        //compare book barcode to find if any books is lost from the shelf
        private void compareBarCode()
        {
            MySqlDataAdapter barcodelayerInfo = new MySqlDataAdapter("SELECT barcode FROM book", mycon);//use barcode from the result of post application of a layer
            MySqlDataAdapter barcodebookInfo = new MySqlDataAdapter("SELECT barcode FROM bookread", mycon);//use barcode from one book
            DataTable dtLayer = new DataTable();
            DataTable dtBook = new DataTable();

            barcodelayerInfo.Fill(dtLayer);
            barcodebookInfo.Fill(dtBook);

            bool isLost = true;
            string resultBook = " ";//unique barcode from the book 
            string resultLayer = " ";//total barcode from one shelf
            string resultLoss = " ";
            string title = " ";//book name 

            //compare barcode by using two loops
            foreach (DataRow layer in dtLayer.Rows)
            {
                resultLayer = layer["barcode"].ToString();
                foreach (DataRow book in dtBook.Rows)
                {
                    resultBook = book["barcode"].ToString();
                    if (resultLayer.Equals(resultBook))
                    {
                        isLost = false;
                        break;
                    }
                }

                //if the book is missing
                if (isLost == true)
                {
                    resultLoss = layer["barcode"].ToString();
                    if (barcodeResultInDB(resultLoss) == false)//whether barcode result is in database
                    {
                        try
                        {
                            //get book name 'title' from database which store by fetch layercode
                            MySqlDataAdapter fetchTitle = new MySqlDataAdapter("SELECT title FROM book WHERE barcode = '" + resultLoss + "'", mycon);
                            DataTable dtTitle = new DataTable();
                            fetchTitle.Fill(dtTitle);
                            foreach (DataRow dr in dtTitle.Rows)
                            {
                                title = dr["title"].ToString();
                            }

                            //store compare result in database
                            string storeReport = "INSERT INTO compareBarcode (barcode, title) VALUES ('" + resultLoss + "',\"" + title + "\")";
                            MySqlCommand store = new MySqlCommand(storeReport, mycon);
                            store.ExecuteNonQuery();

                        }
                        catch(Exception ex)
                        {
                            MessageBox.Show(ex.Message);
                        }
                        
                    }
                }
                isLost = true;
            }

            

        }

        //use layercode to find if any books exist in wrong shelf or means find the book which is not belonged to this shelf
        private void compareLayerCode()
        {
            MySqlDataAdapter layercodelayerInfo = new MySqlDataAdapter("SELECT layercode FROM layer", mycon);
            MySqlDataAdapter layercodeBookInfo = new MySqlDataAdapter("SELECT * FROM bookread", mycon);//select all to get layercode and barcode

            DataTable dt2Layer = new DataTable();
            DataTable dt2Book = new DataTable();

            layercodeBookInfo.Fill(dt2Book);
            layercodelayerInfo.Fill(dt2Layer);

            bool isMore = true;
            String resultMore = " ";
            String layercodeLayer = " ";//layer code from layer
            String layercodeBook = " ";//layer code from book
            String title = " ";//book name
            String resultBarcode = " ";//barcode of the extra books

            //compare layercode by using two loops
            foreach (DataRow book in dt2Book.Rows)
            {
                layercodeLayer = book["layercode"].ToString();
                resultBarcode = book["barcode"].ToString();
                foreach (DataRow layer in dt2Layer.Rows)
                {
                    layercodeBook = layer["layercode"].ToString();
                    if (layercodeBook.Equals(layercodeLayer))
                    {
                        isMore = false;
                        break;
                    }

                    //if the book is extra book from the shelf
                    if (isMore == true)
                    {
                        resultMore = book["layercode"].ToString();
                        if (layercodeResultInDB(resultMore) == false)
                        {
                            try
                            {
                                //testfunction fetchtitle
                                MySqlDataAdapter fetchTitle = new MySqlDataAdapter("SELECT title FROM book WHERE barcode = '" + resultMore + "'", mycon);
                                DataTable dtTitle = new DataTable();
                                fetchTitle.Fill(dtTitle);
                                foreach (DataRow dr in dtTitle.Rows)
                                {
                                    title = dr["title"].ToString();
                                }

                                //store barcode and title in database
                                string storeReport = "INSERT INTO compareLayercode (barcode, title) VALUES ('" + resultBarcode + "',\"" + title + "\")";
                                MySqlCommand store = new MySqlCommand(storeReport, mycon);
                                store.ExecuteNonQuery();
                            }
                            catch
                            {
                                //MessageBox.Show("Store compare more result error");
                            }
                        }
                    }
                }
                isMore = true;
            }
        }
        
        //whether barcode result is in datadase 
        private bool barcodeResultInDB(string barcode)
        {
            MySqlDataAdapter mysda = new MySqlDataAdapter("SELECT * FROM compareBarcode", mycon);
            //compareBarcode database is not created yet
            DataTable dt = new DataTable();
            mysda.Fill(dt);
            string result = " ";
            foreach (DataRow book in dt.Rows)
            {
                result = book["barcode"].ToString();
                if (result.Equals(barcode))
                {
                    return true;
                }
            }
            return false;
        }

        //whether layercode result is in database
        private bool layercodeResultInDB(string layercode)
        {
            MySqlDataAdapter mysda = new MySqlDataAdapter("SELECT LayerCode FROM layer", mycon);
            //compare layercode result is not created yet
            DataTable dt = new DataTable();
            mysda.Fill(dt);
            string result = " ";
            foreach (DataRow book in dt.Rows)
            {
                result = book["LayerCode"].ToString();
                if (result.Equals(layercode))
                {
                    return true;
                }
            }
            return false;
        }

        //testfunction could be deleted
        //private void compare()
        //{
        //    MySqlDataAdapter myTest = new MySqlDataAdapter("SELECT EPC FROM testlibraryreader", mycon);
        //    MySqlDataAdapter myTotal = new MySqlDataAdapter("SELECT EPC FROM totalstorage", mycon);

        //    DataTable dtTest = new DataTable();
        //    DataTable dtTotal = new DataTable();

        //    myTest.Fill(dtTest);
        //    myTotal.Fill(dtTotal);

        //    bool isLost = true;
        //    string resultTest = " ";
        //    string resultTotal = " ";

        //    string resultLoss = " ";
        //    //string resultReport = " ";
        //    foreach (DataRow bookTotal in dtTotal.Rows)
        //    {
        //        resultTotal = bookTotal["EPC"].ToString();
        //        foreach (DataRow bookTest in dtTest.Rows)
        //        {
        //            resultTest = bookTest["EPC"].ToString();
        //            if (resultTotal.Equals(resultTest))
        //            {
        //                isLost = false;
        //                break;
        //            }

        //        }
        //        if (isLost == true)
        //        {
        //            resultLoss = bookTotal["EPC"].ToString();
        //            if (whetherResultInDB(resultLoss) == false)
        //            {
        //                try
        //                {
        //                    mycon.Open();
        //                    string storeReport = "insert into compareresult (EPC) VALUES ('" + resultLoss + "')";
        //                    MySqlCommand store = new MySqlCommand(storeReport, mycon);
        //                    store.ExecuteNonQuery();
        //                    store.Dispose();
        //                    mycon.Close();
        //                }
        //                catch
        //                {
        //                    MessageBox.Show("Report generate error.");
        //                }

        //            }
        //        }
        //        isLost = true;

        //    }
        //}

        // 改变界面按钮状态
        private void changeCtrlEnable(string state)
        {
            if (this.InvokeRequired)
            {
                changeCtrlEnableHandle h = new changeCtrlEnableHandle(changeCtrlEnableMethod);
                this.BeginInvoke(h, state);
            }
            else
                changeCtrlEnableMethod(state);
        }

        private delegate void changeCtrlEnableHandle(string state);

        private void changeCtrlEnableMethod(string state)
        {
            switch (state)
            {
                case "conn":
                    btnConn.Enabled = false;
                    btnDisconn.Enabled = true;
                    btnScan.Enabled = true;
                    btnStop.Enabled = false;
                    DBclear.Enabled = true;
                    MI_ScanConfig.Enabled = true;
                    MI_ReaderConfig.Enabled = true;
                    //MI_GPIO.Enabled = true;
                    break;
                case "disconn":
                    btnConn.Enabled = true;
                    btnDisconn.Enabled = false;
                    btnScan.Enabled = false;
                    btnStop.Enabled = false;
                    MI_ScanConfig.Enabled = false;
                    MI_ReaderConfig.Enabled = false;
                    //MI_GPIO.Enabled = false;
                    break;
                case "scan":
                    btnConn.Enabled = false;
                    btnDisconn.Enabled = true;
                    btnScan.Enabled = false;
                    btnStop.Enabled = true;
                    DBclear.Enabled = false;
                    MI_ScanConfig.Enabled = false;
                    MI_ReaderConfig.Enabled = false;
                    //MI_GPIO.Enabled = false;
                    break;
            }
        }

        // 断开连接
        private void btnDisconn_Click(object sender, EventArgs e)
        {
            if (reader != null)
            {
                reader.OnMessageNotificationReceived -= new Invengo.NetAPI.Core.MessageNotificationReceivedHandle(reader_OnMessageNotificationReceived);
                reader.Disconnect();
            }
            changeCtrlEnable("disconn");
            lblMsg.Text = "断开连接";
        }

        // 退出
        private void btnExit_Click(object sender, EventArgs e)
        {
            btnStop_Click(sender, e);
            this.Close();
        }

        // 扫描标签
        private void btnScan_Click(object sender, EventArgs e)
        {
            btnClean_Click(sender, e);

            if (reader != null && reader.IsConnected)
            {
                if (reader.Send(scanMsg))
                {
                    changeCtrlEnable("scan");
                    lblMsg.Text = "Reading tags...";
                }
            }
            //time control function
            time_period();

        }

        private void timer_Tick(object sender, EventArgs e)
        {
            //this.timer.Enabled = false;
            if (timeCount < 3)
            {
                timeCount++;
                //compareBarCode();
                //compareLayerCode();
                //MessageBox.Show("Comparing...");
                //MySqlCommand delete = new MySqlCommand("truncate table layer; truncate table book; truncate table bookread;", mycon);
                //delete.ExecuteNonQuery();
            }
            else
            {
                timer.Enabled = false;//close the function
            }
        }

        private void time_period()
        {
            timer = new System.Timers.Timer();
            timer.Interval = 13000;
            timer.Elapsed += new System.Timers.ElapsedEventHandler(timer_Tick);
            timer.Enabled = true;
        }

        // 停止扫描
        private void btnStop_Click(object sender, EventArgs e)
        {
            if (reader != null && reader.IsConnected)
            {
                if (reader.Send(new IRP1.PowerOff()))//发送关功放消息
                {
                    changeCtrlEnable("conn");
                    lblMsg.Text = "Stop reading tags";
                }
            }
        }

        // 清空数据
        private void btnClean_Click(object sender, EventArgs e)
        {
            lock (lockobj)
            {
                myDt.Rows.Clear();
            }
        }

        // GPIO
        private void MI_GPIO_Click(object sender, EventArgs e)
        {
            FormGPIO frm = new FormGPIO(reader, scanMsg);
            frm.ShowDialog();
            frm.Dispose();
        }

        private void MI_Help_Click(object sender, EventArgs e)
        {
            FormHelp frm = new FormHelp();
            frm.ShowDialog();
            frm.Dispose();
        }

        private void btnArmStop_Click(object sender, EventArgs e)//r
        {

            PortWrite("F");

        }

        private void btnArmUp_Click(object sender, EventArgs e)//u
        {
            PortWrite("U");
        }

        private void btnArmDown_Click(object sender, EventArgs e)//d
        {
            PortWrite("C");
        }

        private void PortWrite(string message)
        {
            if (port != null && port.IsOpen)
            {
                port.Write(message);
                //port.WriteLine(message);
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            PortWrite("K");
        }

        private void Button2_Click(object sender, EventArgs e)
        {
            detailDB detailDBform = new detailDB();
            detailDBform.ShowDialog();
        }

        private void BtnExecute_Click(object sender, EventArgs e)
        {
            compareReport detailReportform = new compareReport();
            detailReportform.ShowDialog();
        }

        private void FormMain_Load(object sender, EventArgs e)
        {

        }

        private void DBclear_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Do you really want to clear all data in database?", "warning", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                MySqlCommand delete = new MySqlCommand("truncate table layer; truncate table book; truncate table bookread; truncate table comparebarcode; truncate table comparelayercode;", mycon); 
                delete.ExecuteNonQuery();
            }
            else
            {
                return;
            }

        }
        
        private void MI_ReaderConfig_Click_1(object sender, EventArgs e)
        {
            if (frmScanConfig == null)
                frmScanConfig = new FormScanConfig(reader);
            if (frmScanConfig.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                scanMsg = frmScanConfig.msg;
        }
        FormScanConfig frmScanConfig = null;
        private void MI_ScanConfig_Click_1(object sender, EventArgs e)
        {
            if (frmScanConfig == null)
                frmScanConfig = new FormScanConfig(reader);
            if (frmScanConfig.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                scanMsg = frmScanConfig.msg;
        }

        private void ToolStripMenuItem1_Click(object sender, EventArgs e)
        {

        }

        private void BTN_compare_Click(object sender, EventArgs e)
        {
            compareBarCode();
            compareLayerCode();
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }
    }
}
