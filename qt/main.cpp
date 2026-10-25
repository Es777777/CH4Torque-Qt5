#include <QtWidgets>
#include <QtSerialPort/QSerialPort>
#include <QtSerialPort/QSerialPortInfo>
#include <QtEndian>
#include <cmath>
#include <cstring>

static quint16 crc16(const QByteArray &data) {
    quint16 crc = 0xffff;
    for (unsigned char b : data) {
        crc ^= b;
        for (int i = 0; i < 8; ++i) crc = crc & 1 ? (crc >> 1) ^ 0xa001 : crc >> 1;
    }
    return crc;
}

class Plot : public QWidget {
    Q_OBJECT
public:
    explicit Plot(QWidget *p = nullptr) : QWidget(p) { setMinimumHeight(240); }
    void append(double v) { points.push_back(qMakePair(QDateTime::currentMSecsSinceEpoch(), v)); while (!points.isEmpty() && points.first().first < points.last().first - 60000) points.removeFirst(); update(); }
    void clear() { points.clear(); update(); }
protected:
    void paintEvent(QPaintEvent *) override {
        QPainter p(this); p.setRenderHint(QPainter::Antialiasing); p.fillRect(rect(), Qt::white);
        QRectF area = rect().adjusted(58, 24, -18, -36); p.setPen(QColor("#e6ebef"));
        for (int i=0;i<=4;i++) p.drawLine(QPointF(area.left(), area.top()+area.height()*i/4), QPointF(area.right(), area.top()+area.height()*i/4));
        p.setPen(QColor("#536575")); p.drawText(12, 18, "最近 60 秒  ·  力矩趋势");
        if (points.isEmpty()) { p.drawText(area, Qt::AlignCenter, "等待 CH4 数据"); return; }
        double lo=points.first().second, hi=lo; for (auto x: points) { lo=qMin(lo,x.second); hi=qMax(hi,x.second); }
        double pad=qMax((hi-lo)*.12, qMax(std::abs(hi)*.02, .001)); lo-=pad; hi+=pad;
        p.setPen(QColor("#71808d")); for(int i=0;i<=4;i++) p.drawText(QRectF(0,area.top()+area.height()*i/4-8,50,18),Qt::AlignRight,QString::number(hi-(hi-lo)*i/4,'g',4));
        QPainterPath path; const qint64 end=points.last().first, begin=end-60000;
        for(int i=0;i<points.size();i++){ double x=area.left()+qBound(0.0,(points[i].first-begin)/60000.0,1.0)*area.width(); double y=area.bottom()-(points[i].second-lo)/(hi-lo)*area.height(); if(i==0)path.moveTo(x,y);else path.lineTo(x,y); }
        p.setPen(QPen(QColor("#00877f"),2)); p.drawPath(path);
    }
private: QVector<QPair<qint64,double>> points;
};

class Window : public QMainWindow {
    Q_OBJECT
public:
    Window() {
        setWindowTitle("CH4 力矩采集 · Qt 5"); resize(1180,780); setMinimumSize(900,640);
        auto *root=new QWidget; auto *outer=new QVBoxLayout(root); outer->setContentsMargins(0,0,0,0); outer->setSpacing(0);
        auto *head=new QWidget; head->setObjectName("header"); auto *hl=new QHBoxLayout(head); auto *brand=new QLabel("CH4 力矩采集"); brand->setObjectName("brand"); hl->addWidget(brand); hl->addStretch(); status=new QLabel("未连接"); status->setObjectName("status"); hl->addWidget(status); outer->addWidget(head);
        auto *bar=new QHBoxLayout; bar->setContentsMargins(16,12,16,10); outer->addLayout(bar);
        port=new QComboBox; refreshPorts(); auto *refresh=new QPushButton("刷新串口"); connect(refresh,&QPushButton::clicked,this,&Window::refreshPorts);
        baud=new QComboBox; baud->addItems({"9600","19200","38400","57600","115200"}); baud->setCurrentText("115200");
        station=new QSpinBox; station->setRange(1,247); station->setValue(1);
        address=new QLineEdit("6"); address->setMaximumWidth(70); type=new QComboBox; type->addItems({"Int16","UInt16","Int32","UInt32","Float32"}); type->setCurrentText("Int32");
        order=new QComboBox; order->addItems({"AB","BA","ABCD","BADC","CDAB","DCBA"}); order->setCurrentText("CDAB");
        interval=new QSpinBox; interval->setRange(50,10000); interval->setValue(100); interval->setSuffix(" ms");
        addField(bar,"串口",port); bar->addWidget(refresh); addField(bar,"波特率",baud); addField(bar,"站号",station); addField(bar,"CH4 地址",address); addField(bar,"数据类型",type); addField(bar,"字节序",order); addField(bar,"周期",interval);
        auto *actions=new QHBoxLayout; actions->setContentsMargins(16,0,16,12); outer->addLayout(actions);
        start=new QPushButton("开始只读采集"); start->setObjectName("primary"); stop=new QPushButton("停止"); demo=new QPushButton("演示数据"); record=new QPushButton("开始 CSV 记录"); auto *save=new QPushButton("保存设置");
        actions->addWidget(start); actions->addWidget(stop); actions->addWidget(demo); actions->addWidget(record); actions->addStretch(); actions->addWidget(save);
        auto *body=new QHBoxLayout; body->setContentsMargins(16,0,16,12); body->setSpacing(12); outer->addLayout(body,1);
        auto *left=new QVBoxLayout; body->addLayout(left,0); left->setSpacing(10);
        auto *valueBox=new QFrame; valueBox->setObjectName("valueBox"); valueBox->setMinimumWidth(300); auto *vl=new QVBoxLayout(valueBox); vl->addWidget(new QLabel("CH4 当前读数")); value=new QLabel("--"); value->setObjectName("value"); vl->addWidget(value); unit=new QLabel("原始值 / 未标定"); unit->setObjectName("unit"); vl->addWidget(unit); left->addWidget(valueBox);
        raw=new QCheckBox("显示原始值（未标定）"); raw->setChecked(true); left->addWidget(raw);
        auto *safety=new QLabel("协议固定为 Modbus RTU 功能码 03。仅读取配置的 CH4 地址；不会写入设备或执行清零。\n\n原始数值未经力矩标定，不能直接作为 N·m 测量结果。请先核对站号、地址、数据类型和字节序。"); safety->setWordWrap(true); safety->setObjectName("note"); safety->setMinimumWidth(300); left->addWidget(safety); left->addStretch();
        auto *right=new QVBoxLayout; body->addLayout(right,1); plot=new Plot; right->addWidget(plot,1); log=new QPlainTextEdit; log->setReadOnly(true); log->setMaximumBlockCount(500); log->setPlaceholderText("通信日志"); right->addWidget(log,0);
        setCentralWidget(root); setStyleSheet("QMainWindow{background:#f2f5f7} #header{background:#172b3d;padding:8px 18px} #brand{color:white;font-size:22px;font-weight:600} #status{color:#c6dbdc} QPushButton{padding:8px 14px;background:white;border:1px solid #ccd5dc;border-radius:4px;color:#253746} QPushButton:hover{background:#eef4f4} QPushButton#primary{background:#00877f;color:white;border:0;font-weight:600} QComboBox,QLineEdit,QSpinBox{padding:6px;border:1px solid #ccd5dc;background:white;border-radius:3px} #valueBox{background:white;border:1px solid #e1e7eb;border-radius:4px;padding:14px} #value{font-size:44px;font-weight:600;color:#00877f} #unit{color:#647482} #note{color:#526473;background:#e8eef1;padding:14px;border-radius:4px} QPlainTextEdit{background:white;border:1px solid #e1e7eb;font-family:Consolas,monospace}");
        serial=new QSerialPort(this); timer=new QTimer(this); connect(timer,&QTimer::timeout,this,&Window::poll); connect(serial,&QSerialPort::readyRead,this,&Window::readData);
        connect(start,&QPushButton::clicked,this,&Window::begin); connect(stop,&QPushButton::clicked,this,&Window::end); connect(demo,&QPushButton::clicked,this,&Window::toggleDemo); connect(record,&QPushButton::clicked,this,&Window::toggleRecord); connect(save,&QPushButton::clicked,this,&Window::saveSettings);
        connect(&demoTimer,&QTimer::timeout,this,[this]{double v=12+5*std::sin(QDateTime::currentMSecsSinceEpoch()/1400.0);showValue(v);plot->append(v);});
        loadSettings();
    }
private slots:
    void refreshPorts(){QString old=port?port->currentText():QString(); if(!port)return; port->clear(); for(auto i:QSerialPortInfo::availablePorts())port->addItem(i.portName()); int n=port->findText(old); if(n>=0)port->setCurrentIndex(n);}
    void begin(){if(!valid())return; QSerialPort::BaudRate b=static_cast<QSerialPort::BaudRate>(baud->currentText().toInt()); serial->setPortName(port->currentText()); serial->setBaudRate(b); serial->setDataBits(QSerialPort::Data8); serial->setParity(QSerialPort::NoParity); serial->setStopBits(QSerialPort::OneStop); if(!serial->open(QIODevice::ReadWrite)){QMessageBox::critical(this,"串口打开失败",serial->errorString());return;} rx.clear(); timer->start(interval->value()); status->setText("只读采集中 · "+port->portName()); log->appendPlainText("串口已打开；仅发送功能码 03。 "+QDateTime::currentDateTime().toString(Qt::ISODate)); start->setEnabled(false); }
    void end(){timer->stop(); if(serial->isOpen())serial->close(); status->setText("未连接"); start->setEnabled(true); if(demoTimer.isActive())demoTimer.stop(); log->appendPlainText("采集已停止，串口已释放。");}
    void poll(){if(serial->bytesAvailable()||!rx.isEmpty())return; bool ok=false; int a=address->text().toInt(&ok,0); if(!ok||a<0||a>65535){end();return;} QByteArray q; q.append(char(station->value()));q.append(char(3));q.append(char(a>>8));q.append(char(a));int count=(type->currentText().endsWith("32")||type->currentText()=="Float32")?2:1;q.append(char(0));q.append(char(count)); quint16 c=crc16(q);q.append(char(c));q.append(char(c>>8)); serial->write(q); }
    void readData(){rx+=serial->readAll(); int count=(type->currentText().endsWith("32")||type->currentText()=="Float32")?2:1; int len=5+count*2; if(rx.size()<3)return; if((unsigned char)rx[1]==0x83)len=5; if(rx.size()<len)return; QByteArray f=rx.left(len);rx.remove(0,len);quint16 c=crc16(f.left(len-2)); if((unsigned char)f[len-2]!=(c&255)||(unsigned char)f[len-1]!=(c>>8)||(unsigned char)f[0]!=station->value()){log->appendPlainText("响应 CRC/站号无效，已丢弃");return;} if((unsigned char)f[1]==0x83){log->appendPlainText("设备返回 Modbus 异常");return;}if((unsigned char)f[1]!=3||(unsigned char)f[2]!=count*2){log->appendPlainText("响应功能码或长度无效");return;} QByteArray payload=f.mid(3,count*2), canonical=count==1?"AB":"ABCD", ord=order->currentText().toLatin1(); if(ord.size()!=canonical.size()){log->appendPlainText("字节序与数据宽度不匹配");return;} QByteArray b(canonical.size(),0); for(int i=0;i<canonical.size();i++){int at=ord.indexOf(canonical[i]);if(at<0){log->appendPlainText("无效字节序");return;}b[i]=payload[at];} quint32 n=0;for(unsigned char x:b)n=(n<<8)|x; double v=0;QString t=type->currentText(); if(t=="Int16"&&b.size()==2)v=qint16(n);else if(t=="UInt16"&&b.size()==2)v=n;else if(t=="Int32"&&b.size()==4)v=qint32(n);else if(t=="UInt32"&&b.size()==4)v=n;else if(t=="Float32"&&b.size()==4){quint32 bits=qToLittleEndian(n);float f;memcpy(&f,&bits,4);v=f;}else{log->appendPlainText("数据类型与寄存器数量不匹配");return;} if(!std::isfinite(v)){log->appendPlainText("无效非有限数值");return;} showValue(v);plot->append(v);log->appendPlainText("RX "+f.toHex(' ').toUpper()+"  RAW="+QString::number(v,'g',10));if(csv.isOpen()){QTextStream s(&csv);s<<QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs)<<","<<QString::number(v,'g',17)<<",DEVICE_RAW_UNCALIBRATED\n";s.flush();}}
    void toggleDemo(){end();demoTimer.isActive()?(demoTimer.stop(),demo->setText("演示数据")):(plot->clear(),demoTimer.start(150),demo->setText("停止演示"),status->setText("模拟数据 · 非实测"));}
    void toggleRecord(){if(csv.isOpen()){csv.close();record->setText("开始 CSV 记录");return;}QString p=QFileDialog::getSaveFileName(this,"记录 CSV",QDir::homePath()+"/CH4Torque.csv","CSV (*.csv)");if(p.isEmpty())return;csv.setFileName(p);if(!csv.open(QIODevice::WriteOnly|QIODevice::Text)){QMessageBox::warning(this,"无法记录",csv.errorString());return;}QTextStream s(&csv);s<<"timestamp_utc,raw_value,mode\n";record->setText("停止 CSV 记录");}
    void saveSettings(){QSettings s("CH4Torque","Qt5");s.setValue("port",port->currentText());s.setValue("baud",baud->currentText());s.setValue("station",station->value());s.setValue("address",address->text());s.setValue("type",type->currentText());s.setValue("order",order->currentText());s.setValue("interval",interval->value());status->setText("设置已保存");}
private:
    void addField(QHBoxLayout *l,const QString &label,QWidget *w){auto *box=new QWidget;auto *v=new QVBoxLayout(box);v->setContentsMargins(3,0,3,0);v->setSpacing(3);auto *t=new QLabel(label);t->setStyleSheet("color:#647482;font-size:11px");v->addWidget(t);v->addWidget(w);l->addWidget(box);}
    bool valid(){bool ok=false;int a=address->text().toInt(&ok,0);if(!ok||a<0||a>65535){QMessageBox::warning(this,"参数无效","CH4 协议地址必须为 0 至 65535。");return false;}if((type->currentText().endsWith("32")||type->currentText()=="Float32")&&order->currentText().size()!=4){QMessageBox::warning(this,"参数无效","32 位数据必须选择四字节序。");return false;}if(!(type->currentText().endsWith("32")||type->currentText()=="Float32")&&order->currentText().size()!=2){QMessageBox::warning(this,"参数无效","16 位数据必须选择两字节序。");return false;}return !port->currentText().isEmpty();}
    void showValue(double v){value->setText(QString::number(v,'g',9));unit->setText(raw->isChecked()?"原始值 / 未标定":"设备读数");}
    void loadSettings(){QSettings s("CH4Torque","Qt5");refreshPorts();port->setCurrentText(s.value("port").toString());baud->setCurrentText(s.value("baud","115200").toString());station->setValue(s.value("station",1).toInt());address->setText(s.value("address","6").toString());type->setCurrentText(s.value("type","Int32").toString());order->setCurrentText(s.value("order","CDAB").toString());interval->setValue(s.value("interval",100).toInt());}
    QComboBox *port,*baud,*type,*order;QLineEdit *address;QSpinBox *station,*interval;QLabel *status,*value,*unit;QPushButton *start,*stop,*demo,*record;QCheckBox *raw;Plot *plot;QPlainTextEdit *log;QSerialPort *serial;QTimer *timer;QTimer demoTimer;QByteArray rx;QFile csv;
};

int main(int argc,char **argv){QApplication app(argc,argv);app.setOrganizationName("CH4Torque");Window w;w.show();return app.exec();}
#include "main.moc"
