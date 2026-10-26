from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (
    Image,
    KeepTogether,
    PageBreak,
    Paragraph,
    SimpleDocTemplate,
    Spacer,
    Table,
    TableStyle,
)


ROOT = Path(__file__).resolve().parent
OUT = ROOT / "CH4Torque-Qt5-User-Manual-CN.pdf"
SCREENSHOT = ROOT / "CH4Torque-ui-preview.png"
FONT = Path("C:/Windows/Fonts/msyh.ttc")
pdfmetrics.registerFont(TTFont("MicrosoftYaHei", str(FONT), subfontIndex=0))

NAVY = colors.HexColor("#173042")
TEAL = colors.HexColor("#008f89")
INK = colors.HexColor("#263746")
MUTED = colors.HexColor("#607384")
PALE = colors.HexColor("#edf3f5")
WARNING = colors.HexColor("#fff4dd")

styles = getSampleStyleSheet()
styles.add(ParagraphStyle(name="TitleCN", fontName="MicrosoftYaHei", fontSize=25, leading=34, textColor=NAVY, spaceAfter=5 * mm))
styles.add(ParagraphStyle(name="SubCN", fontName="MicrosoftYaHei", fontSize=11, leading=18, textColor=MUTED, spaceAfter=4 * mm))
styles.add(ParagraphStyle(name="H1CN", fontName="MicrosoftYaHei", fontSize=16, leading=23, textColor=NAVY, spaceBefore=2 * mm, spaceAfter=3 * mm, keepWithNext=True))
styles.add(ParagraphStyle(name="H2CN", fontName="MicrosoftYaHei", fontSize=11, leading=17, textColor=TEAL, spaceBefore=2 * mm, spaceAfter=1.5 * mm, keepWithNext=True))
styles.add(ParagraphStyle(name="BodyCN", fontName="MicrosoftYaHei", fontSize=9, leading=15, textColor=INK, spaceAfter=2 * mm))
styles.add(ParagraphStyle(name="SmallCN", fontName="MicrosoftYaHei", fontSize=7.5, leading=12, textColor=MUTED))
styles.add(ParagraphStyle(name="CellCN", fontName="MicrosoftYaHei", fontSize=8, leading=12, textColor=INK))
styles.add(ParagraphStyle(name="CellHeadCN", fontName="MicrosoftYaHei", fontSize=8, leading=12, textColor=colors.white))
styles.add(ParagraphStyle(name="CalloutCN", fontName="MicrosoftYaHei", fontSize=9, leading=15, textColor=INK))


def p(text, style="BodyCN"):
    return Paragraph(text, styles[style])


def numbered(n, text):
    return p(f'<font color="#008f89"><b>{n:02d}</b></font>　{text}')


def callout(text, bg=PALE):
    box = Table([[p(text, "CalloutCN")]], colWidths=[174 * mm])
    box.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, -1), bg),
        ("BOX", (0, 0), (-1, -1), 0.4, colors.HexColor("#d5e0e4")),
        ("LEFTPADDING", (0, 0), (-1, -1), 4 * mm),
        ("RIGHTPADDING", (0, 0), (-1, -1), 4 * mm),
        ("TOPPADDING", (0, 0), (-1, -1), 3 * mm),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 3 * mm),
    ]))
    return box


def chrome(canvas, doc):
    canvas.saveState()
    w, h = A4
    canvas.setFillColor(NAVY)
    canvas.rect(0, h - 13 * mm, w, 13 * mm, stroke=0, fill=1)
    canvas.setFont("MicrosoftYaHei", 8)
    canvas.setFillColor(colors.white)
    canvas.drawString(18 * mm, h - 8.5 * mm, "CH4 力矩采集上位机  |  Qt 5 操作说明")
    canvas.setStrokeColor(colors.HexColor("#dce4e8"))
    canvas.line(18 * mm, 14 * mm, w - 18 * mm, 14 * mm)
    canvas.setFillColor(MUTED)
    canvas.drawString(18 * mm, 9 * mm, "只读采集 · 原始值未标定")
    canvas.drawRightString(w - 18 * mm, 9 * mm, f"{doc.page}")
    canvas.restoreState()


doc = SimpleDocTemplate(
    str(OUT), pagesize=A4,
    rightMargin=18 * mm, leftMargin=18 * mm,
    topMargin=20 * mm, bottomMargin=19 * mm,
    title="CH4 力矩采集上位机 Qt 5 用户说明书",
    author="CH4Torque-Qt5",
    subject="Windows 桌面软件安装、串口采集与数据说明",
)
story = []

story += [Spacer(1, 3 * mm), p("CH4 力矩采集上位机", "TitleCN"),
          p("Qt 5 Windows 版 · 中文用户说明书 · v1.4.1", "SubCN"),
          p("用于中诺 ZNSV6-F4-485 变送器 CH4 通道的只读采集。通过 RS-485 使用 Modbus RTU 功能码 03 读取寄存器，显示当前值、最近 60 秒趋势，并可记录 CSV。", "BodyCN"),
          Spacer(1, 2 * mm)]
img = Image(str(SCREENSHOT), width=174 * mm, height=115 * mm)
img.hAlign = "CENTER"
story += [img, Spacer(1, 2 * mm), p("软件真实界面截图。当前画面标注“模拟数据 · 非实测”，趋势值由软件生成，仅展示控件和趋势图。", "SmallCN"), Spacer(1, 4 * mm),
          callout("<b>重要：</b>当前数据显示为寄存器原始值，尚未完成绝对力矩标定。不得仅凭该读数宣称 N·m 或测量精度。请以厂家寄存器表及已知力矩校准结果为准。", WARNING),
          Spacer(1, 3 * mm), p("适用对象：Windows 10/11 x64；Qt 5 发布包。串口参数需按具体设备确认。", "SmallCN"), PageBreak()]

story += [p("安装与首次采集", "H1CN"),
          p("准备软件", "H2CN"),
          p("在 GitHub Releases 下载 <b>CH4Torque-windows-x64.zip</b> 离线包并解压到普通文件夹。双击 <b>offline-install.bat</b> 启动；需要安装 VC++ 运行库时，按 Windows 提示完成。在线安装入口为 <b>online-install.ps1</b>，会检查环境并从 GitHub/Microsoft 下载所需组件。", "BodyCN"),
          p("连接设备", "H2CN"),
          numbered(1, "按变送器与 USB-RS485 转换器说明接线、供电，并在设备管理器确认 COM 端口。"),
          numbered(2, "关闭其他占用该串口的上位机；同一条 RS-485 总线上只保留一个轮询主站。"),
          numbered(3, "启动程序，在“串口”选择对应 COM 口；点击“刷新”可重新扫描。"),
          numbered(4, "依据厂家寄存器表核对波特率、站号、CH4 起始地址、数据类型、字节序及轮询周期。"),
          numbered(5, "点击“开始采集”，确认状态显示“只读采集中”，并观察读数和通信日志。"),
          numbered(6, "需要保存时点击“开始记录 CSV”；结束后点击“停止 CSV 记录”。点击“停止”关闭串口。"),
          Spacer(1, 2 * mm),
          p("本设备的候选参数", "H2CN")]
rows = [
    [p("参数", "CellHeadCN"), p("当前核对起点", "CellHeadCN"), p("说明", "CellHeadCN")],
    [p("串口格式", "CellCN"), p("115200, 8N1", "CellCN"), p("换接口或设备后重新确认", "CellCN")],
    [p("Modbus", "CellCN"), p("站号 1，功能码 03", "CellCN"), p("只读保持寄存器", "CellCN")],
    [p("CH4 地址", "CellCN"), p("6（十进制）", "CellCN"), p("32 位值连续读取两个寄存器", "CellCN")],
    [p("解码", "CellCN"), p("Int32 / CDAB", "CellCN"), p("按已有设备通信响应核对", "CellCN")],
    [p("轮询周期", "CellCN"), p("100 ms", "CellCN"), p("电脑请求间隔，不是传感器采样率", "CellCN")],
]
t = Table(rows, colWidths=[29 * mm, 48 * mm, 97 * mm], repeatRows=1)
t.setStyle(TableStyle([
    ("BACKGROUND", (0, 0), (-1, 0), NAVY),
    ("BACKGROUND", (0, 1), (-1, -1), colors.white),
    ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, PALE]),
    ("GRID", (0, 0), (-1, -1), 0.35, colors.HexColor("#d5e0e4")),
    ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ("LEFTPADDING", (0, 0), (-1, -1), 2.5 * mm),
    ("RIGHTPADDING", (0, 0), (-1, -1), 2.5 * mm),
    ("TOPPADDING", (0, 0), (-1, -1), 2 * mm),
    ("BOTTOMPADDING", (0, 0), (-1, -1), 2 * mm),
]))
story += [t, Spacer(1, 3 * mm), callout("上述数值是已有设备的候选配置，不代表所有变送器型号/固件。地址或数据格式错误会造成无效或误解码读数；连接前请核对设备资料。"), PageBreak()]

story += [p("读数、记录与问题排查", "H1CN"),
          p("理解界面状态", "H2CN"),
          p("“未连接”表示串口未打开；“只读采集中”表示软件正在请求 CH4 寄存器；“模拟数据 · 非实测”是软件演示值，不来自变送器。原始值不会自动换算为 N·m。趋势图只保留最近 60 秒。", "BodyCN"),
          p("CSV 数据", "H2CN"),
          p("CSV 使用 UTF-8，含 UTC 时间、数值和模式字段。<b>DEVICE_RAW_UNCALIBRATED</b> 为设备原始未标定值；<b>DEMO_SIMULATED</b> 为模拟演示。分析时保留字段，不要把演示数据并入实测数据。长时间采样应先开启 CSV。", "BodyCN"),
          p("标定和单位", "H2CN"),
          p("只有厂家确认寄存器直接输出 N·m，或使用可溯源的已知力矩完成标定后，才可将数值解释为力矩。两点线性标定可记录空载原始值 R0，以及施加已知力矩 T1 时的原始值 R1：<b>K = T1 / (R1 - R0)</b>，<b>b = -K × R0</b>。用其他已知力矩复核；传感器 mV/V 铭牌灵敏度不能直接当成计数到 N·m 的系数。", "BodyCN"),
          p("常见问题", "H2CN")]
faq = [
    [p("现象", "CellHeadCN"), p("检查", "CellHeadCN")],
    [p("端口列表为空", "CellCN"), p("检查 USB-RS485 驱动、线缆和设备管理器，然后刷新端口。", "CellCN")],
    [p("串口打开失败", "CellCN"), p("关闭其他串口软件；拔插转换器后刷新 COM 列表。", "CellCN")],
    [p("没有有效读数", "CellCN"), p("核对 A/B、供电、波特率、站号、地址、数据类型和字节序。", "CellCN")],
    [p("CRC/超时异常", "CellCN"), p("检查线路干扰、连接稳定性、串口占用和设备响应。无效帧不会作为有效读数。", "CellCN")],
    [p("读数不像力矩", "CellCN"), p("先确认寄存器编码和标度；当前原始值未标定，不能据此判断力矩。", "CellCN")],
]
ft = Table(faq, colWidths=[39 * mm, 135 * mm], repeatRows=1)
ft.setStyle(TableStyle([
    ("BACKGROUND", (0, 0), (-1, 0), NAVY),
    ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, PALE]),
    ("GRID", (0, 0), (-1, -1), 0.35, colors.HexColor("#d5e0e4")),
    ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ("LEFTPADDING", (0, 0), (-1, -1), 2.5 * mm),
    ("RIGHTPADDING", (0, 0), (-1, -1), 2.5 * mm),
    ("TOPPADDING", (0, 0), (-1, -1), 2 * mm),
    ("BOTTOMPADDING", (0, 0), (-1, -1), 2 * mm),
]))
story += [ft, Spacer(1, 3 * mm), callout("软件只发送 Modbus RTU 功能码 03，不提供设备写寄存器、清零或标定命令。电气接线、安全防护和量程限制请遵循厂家规范。", WARNING)]

doc.build(story, onFirstPage=chrome, onLaterPages=chrome)
print(OUT)
