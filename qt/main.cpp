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
    explicit Plot(QWidget *parent = nullptr) : QWidget(parent) { setMinimumSize(280, 210); }
    void append(double value) {
        points.push_back(qMakePair(QDateTime::currentMSecsSinceEpoch(), value));
        while (!points.isEmpty() && points.first().first < points.last().first - 60000) points.removeFirst();
        update();
    }
    void clear() { points.clear(); update(); }
    void setDemoPreview() {
        points.clear();
        const qint64 now = QDateTime::currentMSecsSinceEpoch();
        for (int i = 0; i <= 48; ++i) {
            const double phase = i * 0.31;
            points.push_back(qMakePair(now - (48 - i) * 1200, 278.0 + 7.0 * std::sin(phase) + 1.8 * std::sin(phase * 2.4)));
        }
        update();
    }
protected:
    void paintEvent(QPaintEvent *) override {
        QPainter painter(this);
        painter.setRenderHint(QPainter::Antialiasing);
        painter.fillRect(rect(), Qt::white);
        QRectF area = rect().adjusted(62, 34, -16, -32);
        painter.setPen(QColor("#e4e9ed"));
        for (int i = 0; i <= 4; ++i) {
            const qreal y = area.top() + area.height() * i / 4;
            painter.drawLine(QPointF(area.left(), y), QPointF(area.right(), y));
        }
        painter.setPen(QColor("#536575"));
        painter.drawText(QRectF(14, 8, width() - 28, 20), Qt::AlignLeft | Qt::AlignVCenter,
                         QStringLiteral("最近 60 秒 · CH4 力矩趋势"));
        if (points.isEmpty()) {
            painter.drawText(area, Qt::AlignCenter, QStringLiteral("等待 CH4 数据"));
            return;
        }
        double low = points.first().second, high = low;
        for (const auto &point : points) { low = qMin(low, point.second); high = qMax(high, point.second); }
        const double padding = qMax((high - low) * .12, qMax(std::abs(high) * .02, .001));
        low -= padding; high += padding;
        painter.setPen(QColor("#71808d"));
        for (int i = 0; i <= 4; ++i) {
            const qreal y = area.top() + area.height() * i / 4;
            painter.drawText(QRectF(2, y - 9, 52, 18), Qt::AlignRight | Qt::AlignVCenter,
                             QString::number(high - (high - low) * i / 4, 'g', 4));
        }
        QPainterPath path;
        const qint64 end = points.last().first, begin = end - 60000;
        for (int i = 0; i < points.size(); ++i) {
            const double fraction = qBound(0.0, (points[i].first - begin) / 60000.0, 1.0);
            const qreal x = area.left() + fraction * area.width();
            const qreal y = area.bottom() - (points[i].second - low) / (high - low) * area.height();
            if (i == 0) path.moveTo(x, y); else path.lineTo(x, y);
        }
        painter.setPen(QPen(QColor("#00877f"), 2));
        painter.drawPath(path);
    }
private:
    QVector<QPair<qint64, double>> points;
};

class Window : public QMainWindow {
    Q_OBJECT
public:
    Window() {
        setWindowTitle(QStringLiteral("CH4 力矩采集 · Qt 5"));
        resize(1180, 780);
        setMinimumSize(900, 640);

        auto *root = new QWidget;
        auto *outer = new QVBoxLayout(root);
        outer->setContentsMargins(0, 0, 0, 0);
        outer->setSpacing(0);

        auto *header = new QWidget;
        header->setObjectName("header");
        auto *headerLayout = new QHBoxLayout(header);
        auto *brand = new QLabel(QStringLiteral("CH4 力矩采集"));
        brand->setObjectName("brand");
        headerLayout->addWidget(brand);
        headerLayout->addStretch();
        status = new QLabel(QStringLiteral("未连接"));
        status->setObjectName("status");
        status->setMinimumWidth(120);
        status->setAlignment(Qt::AlignRight | Qt::AlignVCenter);
        headerLayout->addWidget(status);
        outer->addWidget(header);

        auto *settings = new QWidget;
        auto *grid = new QGridLayout(settings);
        grid->setContentsMargins(16, 10, 16, 8);
        grid->setHorizontalSpacing(10);
        grid->setVerticalSpacing(7);
        for (int col = 0; col < 4; ++col) grid->setColumnStretch(col, 1);

        port = new QComboBox;
        auto *portField = new QWidget;
        auto *portLayout = new QHBoxLayout(portField);
        portLayout->setContentsMargins(0, 0, 0, 0);
        portLayout->setSpacing(5);
        portLayout->addWidget(port, 1);
        auto *refresh = new QPushButton(QStringLiteral("刷新"));
        refresh->setToolTip(QStringLiteral("重新扫描可用串口"));
        refresh->setMinimumWidth(58);
        portLayout->addWidget(refresh);
        connect(refresh, &QPushButton::clicked, this, &Window::refreshPorts);

        baud = new QComboBox;
        baud->addItems({"9600", "19200", "38400", "57600", "115200"});
        baud->setCurrentText("115200");
        station = new QSpinBox;
        station->setRange(1, 247);
        station->setValue(1);
        interval = new QSpinBox;
        interval->setRange(50, 10000);
        interval->setValue(100);
        interval->setSuffix(QStringLiteral(" ms"));

        address = new QLineEdit("6");
        address->setPlaceholderText(QStringLiteral("十进制或 0x 十六进制"));
        type = new QComboBox;
        type->addItems({"Int16", "UInt16", "Int32", "UInt32", "Float32"});
        type->setCurrentText("Int32");
        order = new QComboBox;
        order->addItems({"AB", "BA", "ABCD", "BADC", "CDAB", "DCBA"});
        order->setCurrentText("CDAB");
        addField(grid, QStringLiteral("串口"), portField, 0, 0);
        addField(grid, QStringLiteral("波特率"), baud, 0, 1);
        addField(grid, QStringLiteral("站号"), station, 0, 2);
        addField(grid, QStringLiteral("轮询周期"), interval, 0, 3);
        addField(grid, QStringLiteral("CH4 起始地址"), address, 1, 0);
        addField(grid, QStringLiteral("数据类型"), type, 1, 1);
        addField(grid, QStringLiteral("字节序"), order, 1, 2);
        auto *protocolNote = new QLabel(QStringLiteral("8N1 · 功能码 03 只读"));
        protocolNote->setObjectName("protocolNote");
        protocolNote->setAlignment(Qt::AlignCenter);
        grid->addWidget(protocolNote, 1, 3);
        outer->addWidget(settings);

        auto *actions = new QHBoxLayout;
        actions->setContentsMargins(16, 2, 16, 10);
        actions->setSpacing(8);
        start = new QPushButton(QStringLiteral("开始采集"));
        start->setObjectName("primary");
        stop = new QPushButton(QStringLiteral("停止"));
        demo = new QPushButton(QStringLiteral("演示数据"));
        record = new QPushButton(QStringLiteral("开始记录 CSV"));
        auto *save = new QPushButton(QStringLiteral("保存设置"));
        actions->addWidget(start);
        actions->addWidget(stop);
        actions->addWidget(demo);
        actions->addWidget(record);
        actions->addStretch();
        actions->addWidget(save);
        outer->addLayout(actions);

        auto *body = new QHBoxLayout;
        body->setContentsMargins(16, 0, 16, 12);
        body->setSpacing(12);
        outer->addLayout(body, 1);
        auto *left = new QVBoxLayout;
        left->setSpacing(10);
        body->addLayout(left, 0);

        auto *valueBox = new QFrame;
        valueBox->setObjectName("valueBox");
        valueBox->setMinimumWidth(270);
        auto *valueLayout = new QVBoxLayout(valueBox);
        valueLayout->setContentsMargins(16, 12, 16, 12);
        auto *valueTitle = new QLabel(QStringLiteral("CH4 当前读数"));
        valueTitle->setObjectName("valueTitle");
        valueLayout->addWidget(valueTitle);
        value = new QLabel("--");
        value->setObjectName("value");
        value->setMinimumHeight(62);
        value->setTextInteractionFlags(Qt::TextSelectableByMouse);
        valueLayout->addWidget(value);
        unit = new QLabel(QStringLiteral("原始值 / 未标定"));
        unit->setObjectName("unit");
        valueLayout->addWidget(unit);
        left->addWidget(valueBox);

        raw = new QCheckBox(QStringLiteral("显示原始值（未标定）"));
        raw->setChecked(true);
        left->addWidget(raw);
        auto *safety = new QLabel(QStringLiteral(
            "本程序固定使用 Modbus RTU 功能码 03，只读取所填 CH4 地址，不向变送器写入数据，也不执行设备清零。\n\n"
            "原始计数未经力矩标定，不能直接作为 N·m 测量结果。请核对站号、寄存器地址、数据类型与字节序。"));
        safety->setWordWrap(true);
        safety->setObjectName("note");
        safety->setMinimumWidth(270);
        left->addWidget(safety);
        left->addStretch();

        auto *right = new QVBoxLayout;
        right->setSpacing(8);
        body->addLayout(right, 1);
        plot = new Plot;
        right->addWidget(plot, 1);
        log = new QPlainTextEdit;
        log->setReadOnly(true);
        log->setMaximumBlockCount(500);
        log->setMinimumHeight(100);
        log->setMaximumHeight(150);
        log->setPlaceholderText(QStringLiteral("通信日志 · TX / RX 与异常信息"));
        log->setObjectName("log");
        right->addWidget(log);
        setCentralWidget(root);

        setStyleSheet(
            "QMainWindow{background:#f2f5f7}"
            "#header{background:#172b3d;padding:7px 18px}"
            "#brand{color:white;font-size:21px;font-weight:600}"
            "#status{color:#d0e4e2;font-size:12px}"
            "QLabel{color:#344653}"
            "QPushButton{min-height:20px;padding:7px 12px;background:white;border:1px solid #ccd5dc;border-radius:4px;color:#253746}"
            "QPushButton:hover{background:#eef4f4}"
            "QPushButton#primary{background:#00877f;color:white;border:0;font-weight:600}"
            "QComboBox,QLineEdit,QSpinBox{min-height:21px;padding:5px 7px;border:1px solid #ccd5dc;background:white;border-radius:3px}"
            "#valueBox{background:white;border:1px solid #e1e7eb;border-radius:4px}"
            "#valueTitle{color:#647482;font-size:12px}"
            "#value{font-size:38px;font-weight:600;color:#00877f}"
            "#unit{color:#647482;font-size:13px}"
            "#note{color:#526473;background:#e8eef1;padding:12px;border-radius:4px}"
            "#protocolNote{color:#526473;font-size:12px;background:#e8eef1;border-radius:3px;padding:5px}"
            "QPlainTextEdit#log{background:white;border:1px solid #e1e7eb;border-radius:3px;font-family:Consolas,monospace;font-size:10px}");

        serial = new QSerialPort(this);
        timer = new QTimer(this);
        connect(timer, &QTimer::timeout, this, &Window::poll);
        connect(serial, &QSerialPort::readyRead, this, &Window::readData);
        connect(start, &QPushButton::clicked, this, &Window::begin);
        connect(stop, &QPushButton::clicked, this, &Window::end);
        connect(demo, &QPushButton::clicked, this, &Window::toggleDemo);
        connect(record, &QPushButton::clicked, this, &Window::toggleRecord);
        connect(save, &QPushButton::clicked, this, &Window::saveSettings);
        connect(&demoTimer, &QTimer::timeout, this, [this] {
            const double sample = 12 + 5 * std::sin(QDateTime::currentMSecsSinceEpoch() / 1400.0);
            showValue(sample);
            plot->append(sample);
        });
        loadSettings();
        refreshPorts();
    }
    void prepareScreenshot() {
        status->setText(QStringLiteral("模拟数据 · 非实测"));
        value->setText(QStringLiteral("284.6"));
        unit->setText(QStringLiteral("模拟读数 / 非实测"));
        plot->setDemoPreview();
        log->setPlainText(QStringLiteral("演示预览：以下内容为模拟数据，不来自变送器。\nTX  示例请求帧（功能码 03）\nRX  示例响应帧（CRC 校验通过）\n原始值=284.6  模式=DEMO_SIMULATED"));
        demo->setText(QStringLiteral("停止演示"));
        start->setEnabled(false);
    }
private slots:
    void refreshPorts() {
        const QString previous = port->currentText();
        port->clear();
        for (const auto &info : QSerialPortInfo::availablePorts()) port->addItem(info.portName());
        const int index = port->findText(previous);
        if (index >= 0) port->setCurrentIndex(index);
    }
    void begin() {
        if (!valid()) return;
        serial->setPortName(port->currentText());
        serial->setBaudRate(static_cast<QSerialPort::BaudRate>(baud->currentText().toInt()));
        serial->setDataBits(QSerialPort::Data8);
        serial->setParity(QSerialPort::NoParity);
        serial->setStopBits(QSerialPort::OneStop);
        if (!serial->open(QIODevice::ReadWrite)) {
            QMessageBox::critical(this, QStringLiteral("串口打开失败"), serial->errorString());
            return;
        }
        rx.clear();
        timer->start(interval->value());
        status->setText(QStringLiteral("只读采集中 · ") + port->currentText());
        log->appendPlainText(QStringLiteral("串口已打开；仅发送功能码 03。") + QDateTime::currentDateTime().toString(Qt::ISODate));
        start->setEnabled(false);
    }
    void end() {
        timer->stop();
        if (serial->isOpen()) serial->close();
        status->setText(QStringLiteral("未连接"));
        start->setEnabled(true);
        if (demoTimer.isActive()) demoTimer.stop();
        demo->setText(QStringLiteral("演示数据"));
        log->appendPlainText(QStringLiteral("采集已停止，串口已释放。"));
    }
    void poll() {
        if (serial->bytesAvailable() || !rx.isEmpty()) return;
        bool ok = false;
        const int registerAddress = address->text().toInt(&ok, 0);
        if (!ok || registerAddress < 0 || registerAddress > 65535) { end(); return; }
        QByteArray request;
        request.append(char(station->value())); request.append(char(3));
        request.append(char(registerAddress >> 8)); request.append(char(registerAddress));
        const int registers = is32Bit() ? 2 : 1;
        request.append(char(0)); request.append(char(registers));
        const quint16 crc = crc16(request);
        request.append(char(crc)); request.append(char(crc >> 8));
        serial->write(request);
        log->appendPlainText(QStringLiteral("TX  ") + request.toHex(' ').toUpper());
    }
    void readData() {
        rx += serial->readAll();
        if (rx.size() < 3) return;
        const int registers = is32Bit() ? 2 : 1;
        int frameLength = 5 + registers * 2;
        if (static_cast<unsigned char>(rx[1]) == 0x83) frameLength = 5;
        if (rx.size() < frameLength) return;
        const QByteArray frame = rx.left(frameLength);
        rx.remove(0, frameLength);
        const quint16 crc = crc16(frame.left(frameLength - 2));
        if (static_cast<unsigned char>(frame[frameLength - 2]) != (crc & 255) ||
            static_cast<unsigned char>(frame[frameLength - 1]) != (crc >> 8) ||
            static_cast<unsigned char>(frame[0]) != station->value()) {
            log->appendPlainText(QStringLiteral("响应 CRC 或站号无效，已丢弃。"));
            return;
        }
        if (static_cast<unsigned char>(frame[1]) == 0x83) {
            log->appendPlainText(QStringLiteral("设备返回 Modbus 异常响应。"));
            return;
        }
        if (static_cast<unsigned char>(frame[1]) != 3 || static_cast<unsigned char>(frame[2]) != registers * 2) {
            log->appendPlainText(QStringLiteral("响应功能码或数据长度不匹配。"));
            return;
        }
        const QByteArray payload = frame.mid(3, registers * 2);
        const QByteArray canonical = registers == 1 ? QByteArray("AB") : QByteArray("ABCD");
        const QByteArray byteOrder = order->currentText().toLatin1();
        if (byteOrder.size() != canonical.size()) { log->appendPlainText(QStringLiteral("字节序与数据宽度不匹配。")); return; }
        QByteArray bytes(canonical.size(), 0);
        for (int i = 0; i < canonical.size(); ++i) {
            const int sourceIndex = byteOrder.indexOf(canonical[i]);
            if (sourceIndex < 0) { log->appendPlainText(QStringLiteral("字节序配置无效。")); return; }
            bytes[i] = payload[sourceIndex];
        }
        quint32 rawValue = 0;
        for (unsigned char byte : bytes) rawValue = (rawValue << 8) | byte;
        double sample = 0;
        const QString dataType = type->currentText();
        if (dataType == "Int16" && bytes.size() == 2) sample = qint16(rawValue);
        else if (dataType == "UInt16" && bytes.size() == 2) sample = rawValue;
        else if (dataType == "Int32" && bytes.size() == 4) sample = qint32(rawValue);
        else if (dataType == "UInt32" && bytes.size() == 4) sample = rawValue;
        else if (dataType == "Float32" && bytes.size() == 4) {
            const quint32 littleEndianBits = qToLittleEndian(rawValue);
            float floatValue;
            std::memcpy(&floatValue, &littleEndianBits, sizeof(floatValue));
            sample = floatValue;
        } else { log->appendPlainText(QStringLiteral("数据类型与寄存器数量不匹配。")); return; }
        if (!std::isfinite(sample)) { log->appendPlainText(QStringLiteral("设备返回非有限数值，已丢弃。")); return; }
        showValue(sample);
        plot->append(sample);
        log->appendPlainText(QStringLiteral("RX  ") + frame.toHex(' ').toUpper() + QStringLiteral("  原始值=") + QString::number(sample, 'g', 10));
        if (csv.isOpen()) {
            QTextStream stream(&csv);
            stream << QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs) << ','
                   << QString::number(sample, 'g', 17) << ",DEVICE_RAW_UNCALIBRATED\n";
            stream.flush();
        }
    }
    void toggleDemo() {
        if (demoTimer.isActive()) {
            demoTimer.stop();
            demo->setText(QStringLiteral("演示数据"));
            status->setText(QStringLiteral("未连接"));
            return;
        }
        end();
        plot->clear();
        demoTimer.start(150);
        demo->setText(QStringLiteral("停止演示"));
        status->setText(QStringLiteral("模拟数据 · 非实测"));
    }
    void toggleRecord() {
        if (csv.isOpen()) {
            csv.close();
            record->setText(QStringLiteral("开始记录 CSV"));
            return;
        }
        const QString path = QFileDialog::getSaveFileName(this, QStringLiteral("保存 CSV 记录"),
            QDir::homePath() + "/CH4Torque.csv", QStringLiteral("CSV 文件 (*.csv)"));
        if (path.isEmpty()) return;
        csv.setFileName(path);
        if (!csv.open(QIODevice::WriteOnly | QIODevice::Text)) {
            QMessageBox::warning(this, QStringLiteral("无法记录"), csv.errorString());
            return;
        }
        QTextStream stream(&csv);
        stream << "timestamp_utc,raw_value,mode\n";
        stream.flush();
        record->setText(QStringLiteral("停止 CSV 记录"));
    }
    void saveSettings() {
        QSettings settings("CH4Torque", "Qt5");
        settings.setValue("port", port->currentText());
        settings.setValue("baud", baud->currentText());
        settings.setValue("station", station->value());
        settings.setValue("address", address->text());
        settings.setValue("type", type->currentText());
        settings.setValue("order", order->currentText());
        settings.setValue("interval", interval->value());
        status->setText(QStringLiteral("设置已保存"));
    }
private:
    void addField(QGridLayout *layout, const QString &label, QWidget *control, int row, int column) {
        auto *field = new QWidget;
        auto *fieldLayout = new QVBoxLayout(field);
        fieldLayout->setContentsMargins(0, 0, 0, 0);
        fieldLayout->setSpacing(3);
        auto *caption = new QLabel(label);
        caption->setObjectName("fieldLabel");
        fieldLayout->addWidget(caption);
        control->setMinimumWidth(90);
        fieldLayout->addWidget(control);
        layout->addWidget(field, row, column);
    }
    bool is32Bit() const { return type->currentText().endsWith("32") || type->currentText() == "Float32"; }
    bool valid() {
        bool ok = false;
        const int registerAddress = address->text().toInt(&ok, 0);
        if (!ok || registerAddress < 0 || registerAddress > 65535) {
            QMessageBox::warning(this, QStringLiteral("参数无效"), QStringLiteral("CH4 起始地址必须为 0 至 65535。"));
            return false;
        }
        if (is32Bit() != (order->currentText().size() == 4)) {
            QMessageBox::warning(this, QStringLiteral("参数无效"), QStringLiteral("16 位数据请选择两字节序，32 位数据请选择四字节序。"));
            return false;
        }
        if (port->currentText().isEmpty()) {
            QMessageBox::warning(this, QStringLiteral("未选择串口"), QStringLiteral("请刷新并选择已连接的串口。"));
            return false;
        }
        return true;
    }
    void showValue(double sample) {
        value->setText(QString::number(sample, 'g', 9));
        unit->setText(raw->isChecked() ? QStringLiteral("原始值 / 未标定") : QStringLiteral("设备读数"));
    }
    void loadSettings() {
        QSettings settings("CH4Torque", "Qt5");
        const QString savedPort = settings.value("port").toString();
        if (!savedPort.isEmpty()) port->setCurrentText(savedPort);
        baud->setCurrentText(settings.value("baud", "115200").toString());
        station->setValue(settings.value("station", 1).toInt());
        address->setText(settings.value("address", "6").toString());
        type->setCurrentText(settings.value("type", "Int32").toString());
        order->setCurrentText(settings.value("order", "CDAB").toString());
        interval->setValue(settings.value("interval", 100).toInt());
    }
    QComboBox *port, *baud, *type, *order;
    QLineEdit *address;
    QSpinBox *station, *interval;
    QLabel *status, *value, *unit;
    QPushButton *start, *stop, *demo, *record;
    QCheckBox *raw;
    Plot *plot;
    QPlainTextEdit *log;
    QSerialPort *serial;
    QTimer *timer;
    QTimer demoTimer;
    QByteArray rx;
    QFile csv;
};

int main(int argc, char **argv) {
    QApplication app(argc, argv);
    app.setOrganizationName("CH4Torque");
    Window window;
    const QStringList arguments = app.arguments();
    const int captureIndex = arguments.indexOf(QStringLiteral("--capture"));
    if (captureIndex >= 0) {
        if (captureIndex + 1 >= arguments.size()) return 2;
        window.prepareScreenshot();
        window.show();
        app.processEvents();
        const QString output = QFileInfo(arguments[captureIndex + 1]).absoluteFilePath();
        const QPixmap image = window.grab();
        if (image.isNull() || image.size().width() < 900 || image.size().height() < 640) return 3;
        if (!image.save(output, "PNG") || !QFileInfo::exists(output)) return 4;
        return 0;
    }
    window.show();
    return app.exec();
}

#include "main.moc"
