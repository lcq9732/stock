using System.Text.Json;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 【新股发行】（2026-10-16，见 doc/ipo-issue-design.md）：151 列全存、上市日写名册、行数对账。
///
/// 样本是 2026-10-16 沙箱里抓的真实响应原样（columns=ALL）：还没上市的 001381 皇冠新材、
/// 2000 年上市的 600285 羚锐制药、北交所 920229 世纪数码、网下配售日带时刻的 002051 中工国际（2006 年）。
/// 存取全部打在临时 SQLite 上（假仓储验不到真 SQL）。
/// </summary>
public class IpoIssueTests : IDisposable
{
    private const string SampleJson = """
[
{"SECUCODE": "001381.SZ", "SECURITY_CODE": "001381", "APPLY_CODE": "001381", "APPLY_DATE": "2026-10-19 00:00:00", "LISTING_DATE": null, "TRADE_MARKET": "深圳证券交易所", "MARKET_TYPE_NEW": "深交所其他", "BALLOT_PAY_DATE": "2026-10-21 00:00:00", "ASSIGN_DATE": "2026-10-20 00:00:00", "ONLINE_ISSUE_DATE": "2026-10-19 00:00:00", "OFFLINE_PLACING__DATE": "2026-10-19 00:00:00", "BALLOT_NUM_DATE": "2026-10-21 00:00:00", "ISSUE_PRICE": 16.98, "PREDICT_ISSUE_PRICE": 0, "PREDICT_ISSUE_PRICE1": 0, "PREDICT_ISSUE_PRICE2": 0, "ISSUE_NUM": 5630.4768, "ONLINE_ISSUE_NUM": 13513000, "ONLINE_APPLY_UPPER": 13500, "AFTER_ISSUE_PE": 25.87, "PREDICT_ISSUE_PE": null, "PREDICT_PE": null, "INDUSTRY_PE_RATIO": 27.65, "INDUSTRY_PE": 27.65, "ONLINE_ISSUE_LWR": null, "TOTAL_RAISE_FUNDS": 9.56055, "ISSUE_WAY": "战略配售,网下询价配售,网上定价发行,市值申购,高管员工参与配售", "INDUSTRY_CODE": "29", "OFFLINE_VAP_RATIO": null, "OFFLINE_VAS_MULTIPLE": null, "ONLINE_VA_NUM": null, "OFFLINE_VAP_OBJECT": null, "ONLINE_VA_SHARES": null, "OFFLINE_VATS": null, "OFFLINE_PLACING_UPPER": 31530815, "BALLOT_NUM": null, "TOTAL_APPLY_NUM": 16885880, "INITIAL_MULTIPLE": 5355.36, "OFFLINE_PLACING_NUM": 31967272, "TOP_APPLY_MARKETCAP": 13.5, "MZYQ_PAY_FUND": 8490, "INDUSTRY_NAME": "橡胶和塑料制品业", "MARKET_CAP_CONFIRMDATE": "T-2日(T:申购日)", "OPEN_PRICE": null, "CLOSE_PRICE": null, "TURNOVERRATE": null, "LD_CLOSE_CHANGE": null, "LD_OPEN_PREMIUM": null, "LD_HIGH_CHANG": null, "OPEN_AVERAGE_PRICE": null, "OPEN_DATE": null, "BVPS": 6.1866, "SECURITY_NAME": "皇冠新材", "TOTAL_CHANGE": null, "PROFIT": null, "CONTINUOUS_1WORD_NUM": "待上市", "MARKET_TYPE": "非科创板", "LIMIT_UP_PRICE": 24.4512, "ORG_TYPE": "非银行股", "TRADE_MARKET_CODE": "069001002001", "CHANGE_RATE": null, "LATELY_PRICE": null, "CHANGE": null, "IS_BALLOT_NUM": 0, "PE_TTM": null, "OPEN_CHANGERATE": null, "SECURITY_TYPE_CODE": "058001001", "CONTINUOUS_N1WORD_NUM": "待上市", "SECURITY_INNER_CODE": "1004487789", "KB_TRADE_DATE": null, "AVERAGE_PRICE": null, "PREDICT_PE_THREE": 22.99, "MARKET": "深交所主板", "ONLINE_APPLY_PRICE": null, "ONLINE_FUND_UPPER": 229230, "FREECAP_AVG_LOWER": 10000000, "OFFLINE_EP_OBJECT": 11994, "INFO_CODE": "AN202607181827095229", "START_DATE": "2026-10-21 00:00:00", "MAIN_BUSINESS": "以功能性新材料为核心,研发、生产及销售工业级胶粘材料、电子级胶粘材料及功能性薄膜材料等功能性复合材料", "EACHBALLOT_SHARES": "500", "UP_DATE": "2026-10-16 00:00:00", "PREDICT_ONAPPLY_UPPER": 13500, "PREDICT_ONFUND_UPPER": 135000, "PAR_VALUE": 1, "ORIG_TRANSFER_NUM": null, "FREECAP_AVG_DATE": "2026-10-12 00:00:00", "PAGE_PREDICT_PRICE1": null, "PAGE_PREDICT_PRICE2": null, "PAGE_PREDICT_PRICE3": null, "PAGE_PREDICT_PE1": null, "PAGE_PREDICT_PE2": null, "PAGE_PREDICT_PE3": null, "LD_AVERAGE_PRICE": null, "IS_VALID": "1", "APPLY_DATE_NUM": 3, "INDUSTRY_PE_NEW": 27.65, "CONTINUOUS_1WORD_PX": null, "TOTAL_ISSUE_NUM": 5630.48, "ONLINE_ES_MULTIPLE": null, "ISSUE_WAY_EXPLAIN": "采用向参与战略配售的投资者定向配售、网下向符合条件的投资者询价配售和网上向社会公众投资者定价发行相结合的方式", "PRICE_WAY": null, "RESULT_NOTICE_DATE": null, "SELECT_LISTING_DATE": null, "ONLINE_PAY_DATE": null, "ONLINE_REFUND_DATE": null, "IS_BEIJING": 0, "PER_SHARES_INCOME": null, "ONLINE_ISSUE_RATIO": null, "OFFFLINE_INITIAL_MULTIPLE": null, "NEWEST_PRICE": null, "ORG_CODE": "10000619247", "TYPE": "001", "AMPLITUDE": null, "REVIEW_DATE": "2026-07-17 00:00:00", "PREDICT_RAISE_FUNDS": 9.186813, "TOTAL_SHARES": 468980000, "RECOMMEND_ORG": "中国国际金融股份有限公司", "UNDERWRITER_ORG": "中国国际金融股份有限公司", "SECURITY_NAME_FULL": "皇冠新材料科技股份有限公司", "ISSUE_STATE": "1", "NETFLOWIN": null, "IPO_YIELD": null, "FREEZE_FUND": null, "BOARD_CODE": "538", "IS_REGISTRATION": "1", "TRADE_MARKET_CST": "0201", "CURRENT_ONEWORD_NUM": null, "IS_UNOPEN": 0, "CONTINUOUS_ONEWORD_NUM": null, "OD_HIGH_CHANGE": null, "SUSPENSION_STATE": null, "REGISTERED_TYPE": null, "IS_1YEAR": 1, "IS_CALENDAR_DATA": "1", "SECURITY_NAME_ABBR": "皇冠新材", "OFFLINE_VATS_NONUNIT": null, "ONLINE_VASHARES_NONUNIT": null, "ENQUIRY_SHARE_NUM": 168858800000, "IS_RISKWARNING": 0, "NETSUMFINA": 8.614225, "DEC_SUMFINA": 9.56055, "TNEW_PRICE": 16.98, "TCHANGE_RATE": 0.0, "IS_REGISTRATION_NEW": "1", "IS_PROFIT": "0", "STAR_SIGN": "3", "ONLINE_APPLY_LOWER": null, "APPLY_AMT_UPPER": null, "OFFFLINE_INITIAL_MULTIPLE_BJ": null, "IS_1YEAR_APPLY": 1},
{"SECUCODE": "600285.SH", "SECURITY_CODE": "600285", "APPLY_CODE": "730285", "APPLY_DATE": "2000-09-14 00:00:00", "LISTING_DATE": "2000-10-18 00:00:00", "TRADE_MARKET": "上海证券交易所", "MARKET_TYPE_NEW": "上交所其他", "BALLOT_PAY_DATE": null, "ASSIGN_DATE": null, "ONLINE_ISSUE_DATE": "2000-09-14 00:00:00", "OFFLINE_PLACING__DATE": null, "BALLOT_NUM_DATE": null, "ISSUE_PRICE": 8.3, "PREDICT_ISSUE_PRICE": 0, "PREDICT_ISSUE_PRICE1": 0.29, "PREDICT_ISSUE_PRICE2": 0, "ISSUE_NUM": 4000, "ONLINE_ISSUE_NUM": null, "ONLINE_APPLY_UPPER": null, "AFTER_ISSUE_PE": 43.68, "PREDICT_ISSUE_PE": null, "PREDICT_PE": null, "INDUSTRY_PE_RATIO": null, "INDUSTRY_PE": 27.06, "ONLINE_ISSUE_LWR": 0.15577002, "TOTAL_RAISE_FUNDS": 3.32, "ISSUE_WAY": "网下定价发行", "INDUSTRY_CODE": null, "OFFLINE_VAP_RATIO": null, "OFFLINE_VAS_MULTIPLE": null, "ONLINE_VA_NUM": null, "OFFLINE_VAP_OBJECT": null, "ONLINE_VA_SHARES": null, "OFFLINE_VATS": null, "OFFLINE_PLACING_UPPER": null, "BALLOT_NUM": null, "TOTAL_APPLY_NUM": null, "INITIAL_MULTIPLE": null, "OFFLINE_PLACING_NUM": null, "TOP_APPLY_MARKETCAP": null, "MZYQ_PAY_FUND": 8300, "INDUSTRY_NAME": null, "MARKET_CAP_CONFIRMDATE": "T-2日(T:申购日)", "OPEN_PRICE": 18.3, "CLOSE_PRICE": 18.59, "TURNOVERRATE": 62.9801, "LD_CLOSE_CHANGE": 123.9759, "LD_OPEN_PREMIUM": 120.481927710843, "LD_HIGH_CHANG": 138.192771084337, "OPEN_AVERAGE_PRICE": 18.9531, "OPEN_DATE": "2000-10-18 00:00:00", "BVPS": null, "SECURITY_NAME": "羚锐制药", "TOTAL_CHANGE": 128.35, "PROFIT": 10650, "CONTINUOUS_1WORD_NUM": "1", "MARKET_TYPE": "非科创板", "LIMIT_UP_PRICE": 11.952, "ORG_TYPE": "非银行股", "TRADE_MARKET_CODE": "069001001001", "CHANGE_RATE": -0.42, "LATELY_PRICE": 21.14, "CHANGE": -0.09, "IS_BALLOT_NUM": 0, "PE_TTM": 86.58355671, "OPEN_CHANGERATE": 0, "SECURITY_TYPE_CODE": "058001001", "CONTINUOUS_N1WORD_NUM": "1", "SECURITY_INNER_CODE": "1000001997", "KB_TRADE_DATE": "2000-10-18 00:00:00", "AVERAGE_PRICE": 18.9531, "PREDICT_PE_THREE": 22.99, "MARKET": "上交所主板", "ONLINE_APPLY_PRICE": null, "ONLINE_FUND_UPPER": null, "FREECAP_AVG_LOWER": null, "OFFLINE_EP_OBJECT": null, "INFO_CODE": null, "START_DATE": null, "MAIN_BUSINESS": "药品的研发、生产和销售", "EACHBALLOT_SHARES": "1000", "UP_DATE": "2026-10-16 00:00:00", "PREDICT_ONAPPLY_UPPER": null, "PREDICT_ONFUND_UPPER": null, "PAR_VALUE": 1, "ORIG_TRANSFER_NUM": null, "FREECAP_AVG_DATE": null, "PAGE_PREDICT_PRICE1": null, "PAGE_PREDICT_PRICE2": null, "PAGE_PREDICT_PRICE3": null, "PAGE_PREDICT_PE1": null, "PAGE_PREDICT_PE2": null, "PAGE_PREDICT_PE3": null, "LD_AVERAGE_PRICE": 18.9531, "IS_VALID": "1", "APPLY_DATE_NUM": 19528, "INDUSTRY_PE_NEW": 27.06, "CONTINUOUS_1WORD_PX": 1, "TOTAL_ISSUE_NUM": 4000, "ONLINE_ES_MULTIPLE": null, "ISSUE_WAY_EXPLAIN": "网上定价发行", "PRICE_WAY": null, "RESULT_NOTICE_DATE": null, "SELECT_LISTING_DATE": null, "ONLINE_PAY_DATE": null, "ONLINE_REFUND_DATE": null, "IS_BEIJING": 0, "PER_SHARES_INCOME": null, "ONLINE_ISSUE_RATIO": null, "OFFFLINE_INITIAL_MULTIPLE": null, "NEWEST_PRICE": null, "ORG_CODE": "10002482", "TYPE": "001", "AMPLITUDE": 20.3614, "REVIEW_DATE": null, "PREDICT_RAISE_FUNDS": 0, "TOTAL_SHARES": 100360000, "RECOMMEND_ORG": "海通证券有限公司,华夏证券有限公司", "UNDERWRITER_ORG": "华夏证券股份有限公司", "SECURITY_NAME_FULL": "河南羚锐制药股份有限公司", "ISSUE_STATE": "1", "NETFLOWIN": null, "IPO_YIELD": 128.350602409639, "FREEZE_FUND": null, "BOARD_CODE": "1040", "IS_REGISTRATION": "0", "TRADE_MARKET_CST": "0101", "CURRENT_ONEWORD_NUM": 0, "IS_UNOPEN": 0, "CONTINUOUS_ONEWORD_NUM": null, "OD_HIGH_CHANGE": 138.19, "SUSPENSION_STATE": null, "REGISTERED_TYPE": null, "IS_1YEAR": 0, "IS_CALENDAR_DATA": "0", "SECURITY_NAME_ABBR": "羚锐制药", "OFFLINE_VATS_NONUNIT": null, "ONLINE_VASHARES_NONUNIT": null, "ENQUIRY_SHARE_NUM": null, "IS_RISKWARNING": 0, "NETSUMFINA": 3.203, "DEC_SUMFINA": 3.32, "TNEW_PRICE": 21.14, "TCHANGE_RATE": 154.7, "IS_REGISTRATION_NEW": "0", "IS_PROFIT": "0", "STAR_SIGN": "3", "ONLINE_APPLY_LOWER": null, "APPLY_AMT_UPPER": null, "OFFFLINE_INITIAL_MULTIPLE_BJ": null, "IS_1YEAR_APPLY": 0},
{"SECUCODE": "920229.BJ", "SECURITY_CODE": "920229", "APPLY_CODE": "920229", "APPLY_DATE": "2026-09-09 00:00:00", "LISTING_DATE": "2026-09-22 00:00:00", "TRADE_MARKET": "北京证券交易所", "MARKET_TYPE_NEW": "北交所", "BALLOT_PAY_DATE": null, "ASSIGN_DATE": null, "ONLINE_ISSUE_DATE": null, "OFFLINE_PLACING__DATE": null, "BALLOT_NUM_DATE": "2026-09-14 00:00:00", "ISSUE_PRICE": 15.67, "PREDICT_ISSUE_PRICE": null, "PREDICT_ISSUE_PRICE1": null, "PREDICT_ISSUE_PRICE2": null, "ISSUE_NUM": 1100, "ONLINE_ISSUE_NUM": 9350000, "ONLINE_APPLY_UPPER": 467500, "AFTER_ISSUE_PE": 13.49, "PREDICT_ISSUE_PE": null, "PREDICT_PE": null, "INDUSTRY_PE_RATIO": null, "INDUSTRY_PE": null, "ONLINE_ISSUE_LWR": 0.01624843, "TOTAL_RAISE_FUNDS": null, "ISSUE_WAY": null, "INDUSTRY_CODE": null, "OFFLINE_VAP_RATIO": null, "OFFLINE_VAS_MULTIPLE": null, "ONLINE_VA_NUM": 679423, "OFFLINE_VAP_OBJECT": null, "ONLINE_VA_SHARES": 57544021500, "OFFLINE_VATS": null, "OFFLINE_PLACING_UPPER": null, "BALLOT_NUM": null, "TOTAL_APPLY_NUM": null, "INITIAL_MULTIPLE": null, "OFFLINE_PLACING_NUM": null, "TOP_APPLY_MARKETCAP": 732.5725, "MZYQ_PAY_FUND": null, "INDUSTRY_NAME": null, "MARKET_CAP_CONFIRMDATE": null, "OPEN_PRICE": 60.9, "CLOSE_PRICE": 132, "TURNOVERRATE": 98.9846, "LD_CLOSE_CHANGE": 742.374, "LD_OPEN_PREMIUM": 288.640714741544, "LD_HIGH_CHANG": null, "OPEN_AVERAGE_PRICE": null, "OPEN_DATE": null, "BVPS": null, "SECURITY_NAME": "世纪数码", "TOTAL_CHANGE": 338.7, "PROFIT": null, "CONTINUOUS_1WORD_NUM": null, "MARKET_TYPE": "北交所", "LIMIT_UP_PRICE": null, "ORG_TYPE": null, "TRADE_MARKET_CODE": "069001017", "CHANGE_RATE": -1.44, "LATELY_PRICE": 37.77, "CHANGE": -0.55, "IS_BALLOT_NUM": null, "PE_TTM": null, "OPEN_CHANGERATE": null, "SECURITY_TYPE_CODE": "058001001", "CONTINUOUS_N1WORD_NUM": null, "SECURITY_INNER_CODE": "1004260537", "KB_TRADE_DATE": null, "AVERAGE_PRICE": null, "PREDICT_PE_THREE": null, "MARKET": null, "ONLINE_APPLY_PRICE": null, "ONLINE_FUND_UPPER": null, "FREECAP_AVG_LOWER": null, "OFFLINE_EP_OBJECT": null, "INFO_CODE": "AN202609071829102548", "START_DATE": null, "MAIN_BUSINESS": "数码喷印设备及相关配件耗材的研发、生产及销售", "EACHBALLOT_SHARES": null, "UP_DATE": null, "PREDICT_ONAPPLY_UPPER": null, "PREDICT_ONFUND_UPPER": null, "PAR_VALUE": null, "ORIG_TRANSFER_NUM": null, "FREECAP_AVG_DATE": null, "PAGE_PREDICT_PRICE1": null, "PAGE_PREDICT_PRICE2": null, "PAGE_PREDICT_PRICE3": null, "PAGE_PREDICT_PE1": null, "PAGE_PREDICT_PE2": null, "PAGE_PREDICT_PE3": null, "LD_AVERAGE_PRICE": null, "IS_VALID": null, "APPLY_DATE_NUM": 10037, "INDUSTRY_PE_NEW": 45.97, "CONTINUOUS_1WORD_PX": null, "TOTAL_ISSUE_NUM": 1100, "ONLINE_ES_MULTIPLE": 6154.4408, "ISSUE_WAY_EXPLAIN": null, "PRICE_WAY": "直接定价", "RESULT_NOTICE_DATE": "2026-09-14 00:00:00", "SELECT_LISTING_DATE": "2026-09-22 00:00:00", "ONLINE_PAY_DATE": "2026-09-09 00:00:00", "ONLINE_REFUND_DATE": "2026-09-11 00:00:00", "IS_BEIJING": 1, "PER_SHARES_INCOME": 5307.5, "ONLINE_ISSUE_RATIO": 0.01624843, "OFFFLINE_INITIAL_MULTIPLE": null, "NEWEST_PRICE": null, "ORG_CODE": "10542745", "TYPE": "002", "AMPLITUDE": 607.1474, "REVIEW_DATE": null, "PREDICT_RAISE_FUNDS": null, "TOTAL_SHARES": null, "RECOMMEND_ORG": null, "UNDERWRITER_ORG": null, "SECURITY_NAME_FULL": null, "ISSUE_STATE": null, "NETFLOWIN": null, "IPO_YIELD": null, "FREEZE_FUND": null, "BOARD_CODE": null, "IS_REGISTRATION": "1", "TRADE_MARKET_CST": "0500", "CURRENT_ONEWORD_NUM": null, "IS_UNOPEN": null, "CONTINUOUS_ONEWORD_NUM": null, "OD_HIGH_CHANGE": null, "SUSPENSION_STATE": null, "REGISTERED_TYPE": null, "IS_1YEAR": 1, "IS_CALENDAR_DATA": null, "SECURITY_NAME_ABBR": "世纪数码", "OFFLINE_VATS_NONUNIT": null, "ONLINE_VASHARES_NONUNIT": null, "ENQUIRY_SHARE_NUM": null, "IS_RISKWARNING": null, "NETSUMFINA": 148708374.56, "DEC_SUMFINA": 172370000, "TNEW_PRICE": 37.77, "TCHANGE_RATE": 141.03, "IS_REGISTRATION_NEW": "1", "IS_PROFIT": null, "STAR_SIGN": null, "ONLINE_APPLY_LOWER": 100, "APPLY_AMT_UPPER": 732.5725, "OFFFLINE_INITIAL_MULTIPLE_BJ": null, "IS_1YEAR_APPLY": 1},
{"SECUCODE": "002051.SZ", "SECURITY_CODE": "002051", "APPLY_CODE": "002051", "APPLY_DATE": "2006-06-05 00:00:00", "LISTING_DATE": "2006-06-19 00:00:00", "TRADE_MARKET": "深圳证券交易所", "MARKET_TYPE_NEW": "深交所其他", "BALLOT_PAY_DATE": "2006-06-05 00:00:00", "ASSIGN_DATE": "2006-06-08 00:00:00", "ONLINE_ISSUE_DATE": "2006-06-05 00:00:00", "OFFLINE_PLACING__DATE": "2006-05-31 09:00:00", "BALLOT_NUM_DATE": "2006-06-09 00:00:00", "ISSUE_PRICE": 7.4, "PREDICT_ISSUE_PRICE": 0, "PREDICT_ISSUE_PRICE1": 7.34, "PREDICT_ISSUE_PRICE2": 0, "ISSUE_NUM": 6000, "ONLINE_ISSUE_NUM": 48000000, "ONLINE_APPLY_UPPER": null, "AFTER_ISSUE_PE": 19.83, "PREDICT_ISSUE_PE": null, "PREDICT_PE": null, "INDUSTRY_PE_RATIO": null, "INDUSTRY_PE": 7.89, "ONLINE_ISSUE_LWR": 0.173614345, "TOTAL_RAISE_FUNDS": 4.44, "ISSUE_WAY": "网下询价配售", "INDUSTRY_CODE": null, "OFFLINE_VAP_RATIO": 0.969775, "OFFLINE_VAS_MULTIPLE": 103.12, "ONLINE_VA_NUM": 1138513, "OFFLINE_VAP_OBJECT": 132, "ONLINE_VA_SHARES": 2764748.5, "OFFLINE_VATS": 124190, "OFFLINE_PLACING_UPPER": 12000000, "BALLOT_NUM": "末\"三\"位数：481；末\"四\"位数：1107,3107,3592,5107,7107,8592,9107；末\"五\"位数：19711,36265,86265；末\"六\"位数：119645,319645,519645,572598,719645,919645；末\"七\"位数：0651429；末\"八\"位数：09939698,17187652,24466211", "TOTAL_APPLY_NUM": null, "INITIAL_MULTIPLE": null, "OFFLINE_PLACING_NUM": 12000000, "TOP_APPLY_MARKETCAP": null, "MZYQ_PAY_FUND": 3700, "INDUSTRY_NAME": null, "MARKET_CAP_CONFIRMDATE": "T-2日(T:申购日)", "OPEN_PRICE": 17.11, "CLOSE_PRICE": 31.97, "TURNOVERRATE": 94.0529, "LD_CLOSE_CHANGE": 332.027, "LD_OPEN_PREMIUM": 131.216216216216, "LD_HIGH_CHANG": 575.675675675676, "OPEN_AVERAGE_PRICE": 18.331, "OPEN_DATE": "2006-06-19 00:00:00", "BVPS": null, "SECURITY_NAME": "中工国际", "TOTAL_CHANGE": 147.72, "PROFIT": 5465, "CONTINUOUS_1WORD_NUM": "1", "MARKET_TYPE": "非科创板", "LIMIT_UP_PRICE": 10.656, "ORG_TYPE": "非银行股", "TRADE_MARKET_CODE": "069001002001", "CHANGE_RATE": 2.33, "LATELY_PRICE": 8.33, "CHANGE": 0.19, "IS_BALLOT_NUM": 1, "PE_TTM": 85.69140242, "OPEN_CHANGERATE": 0, "SECURITY_TYPE_CODE": "058001001", "CONTINUOUS_N1WORD_NUM": "1", "SECURITY_INNER_CODE": "1000000519", "KB_TRADE_DATE": "2006-06-19 00:00:00", "AVERAGE_PRICE": 18.331, "PREDICT_PE_THREE": 22.99, "MARKET": "深交所主板", "ONLINE_APPLY_PRICE": null, "ONLINE_FUND_UPPER": null, "FREECAP_AVG_LOWER": null, "OFFLINE_EP_OBJECT": null, "INFO_CODE": "AN201202260003950636", "START_DATE": "2006-06-05 00:00:00", "MAIN_BUSINESS": "设计咨询与工程承包,先进工程技术装备开发与应用,工程投资与运营", "EACHBALLOT_SHARES": "500", "UP_DATE": "2026-10-16 00:00:00", "PREDICT_ONAPPLY_UPPER": 48000, "PREDICT_ONFUND_UPPER": 480000, "PAR_VALUE": 1, "ORIG_TRANSFER_NUM": null, "FREECAP_AVG_DATE": null, "PAGE_PREDICT_PRICE1": null, "PAGE_PREDICT_PRICE2": null, "PAGE_PREDICT_PRICE3": null, "PAGE_PREDICT_PE1": null, "PAGE_PREDICT_PE2": null, "PAGE_PREDICT_PE3": null, "LD_AVERAGE_PRICE": 18.331, "IS_VALID": "1", "APPLY_DATE_NUM": 17438, "INDUSTRY_PE_NEW": 7.89, "CONTINUOUS_1WORD_PX": 1, "TOTAL_ISSUE_NUM": 6000, "ONLINE_ES_MULTIPLE": 575.99, "ISSUE_WAY_EXPLAIN": "采用网下向配售对象累计投标询价发行与网上资金申购定价发行相结合的方式。\r\n", "PRICE_WAY": null, "RESULT_NOTICE_DATE": null, "SELECT_LISTING_DATE": null, "ONLINE_PAY_DATE": null, "ONLINE_REFUND_DATE": null, "IS_BEIJING": 0, "PER_SHARES_INCOME": null, "ONLINE_ISSUE_RATIO": null, "OFFFLINE_INITIAL_MULTIPLE": null, "NEWEST_PRICE": null, "ORG_CODE": "10010871", "TYPE": "001", "AMPLITUDE": 466.2162, "REVIEW_DATE": "2004-07-27 00:00:00", "PREDICT_RAISE_FUNDS": 4.1964, "TOTAL_SHARES": 190000000, "RECOMMEND_ORG": "国信证券有限责任公司", "UNDERWRITER_ORG": "国信证券股份有限公司", "SECURITY_NAME_FULL": "中工国际工程股份有限公司", "ISSUE_STATE": "1", "NETFLOWIN": null, "IPO_YIELD": 147.716216216216, "FREEZE_FUND": 204591389035.278, "BOARD_CODE": "1248", "IS_REGISTRATION": "0", "TRADE_MARKET_CST": "0201", "CURRENT_ONEWORD_NUM": 0, "IS_UNOPEN": 0, "CONTINUOUS_ONEWORD_NUM": null, "OD_HIGH_CHANGE": 575.68, "SUSPENSION_STATE": null, "REGISTERED_TYPE": null, "IS_1YEAR": 0, "IS_CALENDAR_DATA": "0", "SECURITY_NAME_ABBR": "中工国际", "OFFLINE_VATS_NONUNIT": 1241900000, "ONLINE_VASHARES_NONUNIT": 27647485000, "ENQUIRY_SHARE_NUM": null, "IS_RISKWARNING": 0, "NETSUMFINA": 4.227041, "DEC_SUMFINA": 4.44, "TNEW_PRICE": 8.33, "TCHANGE_RATE": 12.57, "IS_REGISTRATION_NEW": "0", "IS_PROFIT": "0", "STAR_SIGN": "3", "ONLINE_APPLY_LOWER": null, "APPLY_AMT_UPPER": null, "OFFFLINE_INITIAL_MULTIPLE_BJ": null, "IS_1YEAR_APPLY": 0}
]
""";

    private readonly ITestOutputHelper _out;
    private readonly string _dbPath;
    private readonly SqliteIpoIssueRepository _repo;

    public IpoIssueTests(ITestOutputHelper output)
    {
        _out = output;
        _dbPath = Path.Combine(Path.GetTempPath(), $"ipo_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteIpoIssueRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!,
                                             Path.GetFileNameWithoutExtension(_dbPath) + "*"))
            try { File.Delete(f); } catch { /* 还被连接池占着，留给系统清理 */ }
    }

    private static List<JsonElement> Items() =>
        JsonDocument.Parse(SampleJson).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();

    private static List<IpoIssueRow> Rows(IpoFetchStats? stats = null) =>
        Items().Select(i => EastMoneyIpoIssueProvider.Parse(i, stats)!).ToList();

    // ─────────────────── ① 列目录 ───────────────────

    [Fact]
    public void 列目录就是接口的151列_顺序一致()
    {
        var keys = Items()[0].EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(151, IpoIssueColumns.All.Count);
        Assert.Equal(keys, IpoIssueColumns.All.Select(c => c.Api));
    }

    [Fact]
    public void 库里每一列都在_代码存成主键code()
    {
        var got = _repo.Get("nothing");
        Assert.Null(got);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('IpoIssue');";
        var cols = new HashSet<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) cols.Add(r.GetString(0));
        Assert.Contains("code", cols);
        Assert.DoesNotContain("security_code", cols);
        Assert.All(IpoIssueColumns.All, c => Assert.Contains(c.Db, cols));
        Assert.Equal(151 + 1, cols.Count);                  // 151 列（代码就是主键）+ fetched_at
    }

    // ─────────────────── ② 解析 ───────────────────

    [Fact]
    public void 发行价是数_上市日存成日期_还没上市的为空()
    {
        var rows = Rows();
        var lr = rows.Single(r => r.Code == "600285");
        Assert.Equal(8.3, lr.IssuePrice);
        Assert.Equal(new DateOnly(2000, 10, 18), lr.ListingDate);
        Assert.Equal("2000-10-18", lr["LISTING_DATE"]);
        var hg = rows.Single(r => r.Code == "001381");
        Assert.Equal(16.98, hg.IssuePrice);
        Assert.Null(hg.ListingDate);                        // 申购了、还没上市
        Assert.Equal(new DateOnly(2026, 9, 22), rows.Single(r => r.Code == "920229").ListingDate);
    }

    /// <summary>网下配售日在 2006~2008 年的老股里带具体时刻（334 条），截掉就丢信息。</summary>
    [Fact]
    public void 日期带时刻的原样保留()
        => Assert.Equal("2006-05-31 09:00:00", Rows().Single(r => r.Code == "002051")["OFFLINE_PLACING__DATE"]);

    [Fact]
    public void 接口多出来的列点名_数值列收到文本能解析的照解析()
    {
        var stats = new IpoFetchStats();
        var json = """{"SECURITY_CODE":"600000","ISSUE_PRICE":"10.5","NEW_COLUMN_X":1}""";
        var row = EastMoneyIpoIssueProvider.Parse(JsonDocument.Parse(json).RootElement, stats)!;
        Assert.Equal(10.5, row.IssuePrice);
        Assert.Contains("NEW_COLUMN_X", stats.UnknownColumns);
    }

    // ─────────────────── ③ 存取（真 SQLite）───────────────────

    /// <summary>151 列逐格写进去、读回来一样——拼出来的 INSERT 漏一列不会报错、只会静默少存。</summary>
    [Fact]
    public void 每一列都落库_读回来逐格一致()
    {
        var rows = Rows();
        _repo.Upsert(rows);
        foreach (var r in rows)
        {
            var got = _repo.Get(r.Code)!;
            foreach (var c in IpoIssueColumns.All)
            {
                var want = r[c.Api];
                var have = got[c.Api];
                if (want is double dw) Assert.Equal(dw, Convert.ToDouble(have), 10);
                else Assert.Equal(want, have);
            }
        }
    }

    [Fact]
    public void 再写一次覆盖旧值_不多出行()
    {
        var rows = Rows();
        _repo.Upsert(rows);
        var changed = new IpoIssueRow
        {
            Code = "600285",
            Values = new Dictionary<string, object?>(rows.Single(r => r.Code == "600285").Values) { ["TNEW_PRICE"] = 99.9 },
        };
        _repo.Upsert([changed]);
        Assert.Equal(99.9, Convert.ToDouble(_repo.Get("600285")!["TNEW_PRICE"]));
    }

    /// <summary>上市日写名册：只改名册里已有的个股，不新建名册行；值没变不算改。</summary>
    [Fact]
    public void 上市日只写进名册里已有的个股()
    {
        SqliteStockMetaUpsert.Upsert(_dbPath, [("600285", "羚锐制药")], SqliteStockMetaUpsert.TypeStock);
        var rows = Rows();

        Assert.Equal(1, _repo.UpdateListDates(rows));       // 920229/002051 不在名册、001381 没上市
        Assert.Equal(0, _repo.UpdateListDates(rows));       // 没变就不写
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, list_date FROM StockMeta;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(("600285", "2000-10-18"), (r.GetString(0), r.GetString(1)));
        Assert.False(r.Read());                              // 没有新建名册行
    }

    // ─────────────────── ④ 任务 ───────────────────

    private sealed class FakeProvider(IReadOnlyList<IpoIssueRow> rows, int? reported) : IIpoIssueProvider
    {
        public List<DateOnly?> Since { get; } = [];
#pragma warning disable CS0067
        public event Action<string>? OnStatus;
#pragma warning restore CS0067
        public async IAsyncEnumerable<IReadOnlyList<IpoIssueRow>> FetchAsync(
            DateOnly? applySince, IpoFetchStats stats, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Since.Add(applySince);
            stats.ReportedTotal = reported;
            stats.Fetched = rows.Count;
            await Task.Yield();
            yield return rows;
        }
    }

    [Fact]
    public async Task 日常增量回看一年_回补取全量()
    {
        var p = new FakeProvider(Rows(), 4);
        await new IpoIssueTask(p, _repo).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);
        await new IpoIssueTask(p, _repo).RunAsync(new TaskRunArgs(FetchMode.FirstBackfill), CancellationToken.None);

        Assert.Equal(DateOnly.FromDateTime(DateTime.Today).AddDays(-IpoIssueTask.ApplyLookbackDays), p.Since[0]);
        Assert.Null(p.Since[1]);
        Assert.NotNull(_repo.Get("920229"));
    }

    /// <summary>拿到的行数对不上接口自报的总数＝中途悄悄少了，整项报失败（已存的不受影响）。</summary>
    [Fact]
    public async Task 行数对不上就报失败()
    {
        var r = await new IpoIssueTask(new FakeProvider(Rows(), 5), _repo)
            .RunAsync(new TaskRunArgs(FetchMode.FirstBackfill), CancellationToken.None);
        Assert.Equal(TaskState.Failed, r.State);
        Assert.Contains(r.Errors, e => e.Contains("接口说有 5 只、实际拿到 4 只"));
        Assert.NotNull(_repo.Get("600285"));
    }

    [Fact]
    public void 目录里声明了两档_归日更()
    {
        var a = FetchTaskCatalog.All.Single(x => x.Id == FetchActionId.FetchIpoIssue);
        Assert.True(a.SupportedModes.HasFlag(FetchMode.FirstBackfill));
        Assert.Equal(PlanGroupKind.Daily, FetchTaskCatalog.DefaultGroupOf(FetchActionId.FetchIpoIssue));
    }

    // ─────────────────── ⑤ 全量离线核对（设 IPO_FULL_JSON 才跑）───────────────────

    /// <summary>
    /// 拿沙箱抓回来的全量响应（{"order":[...],"rows":[...]}）走一遍产品代码：解析 → 写库 → 读回，
    /// 5,640 行 × 151 列逐格比原始 JSON。零网络。
    ///   dotnet test --filter "FullyQualifiedName~IpoIssueTests.全量" -e IPO_FULL_JSON=路径
    /// </summary>
    [Fact]
    public void 全量离线核对_逐格一致()
    {
        var path = Environment.GetEnvironmentVariable("IPO_FULL_JSON");
        if (string.IsNullOrEmpty(path)) { _out.WriteLine("跳过（设 IPO_FULL_JSON 才跑）"); return; }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var items = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
        var stats = new IpoFetchStats();
        var rows = items.Select(i => EastMoneyIpoIssueProvider.Parse(i, stats)!).ToList();
        _repo.Upsert(rows);

        int cells = 0, bad = 0;
        foreach (var (item, row) in items.Zip(rows))
        {
            var got = _repo.Get(row.Code)!;
            foreach (var p in item.EnumerateObject())
            {
                cells++;
                var have = got[p.Name];
                bool ok = p.Value.ValueKind switch
                {
                    JsonValueKind.Null => have is null,
                    JsonValueKind.Number => have is not null && Math.Abs(Convert.ToDouble(have) - p.Value.GetDouble()) < 1e-9,
                    JsonValueKind.String => p.Value.GetString() is var s
                        && (Equals(have, s) || (s!.EndsWith(" 00:00:00") && Equals(have, s[..10]))),
                    _ => false,
                };
                if (!ok && ++bad <= 10) _out.WriteLine($"{row.Code} {p.Name}: 原 {p.Value} 库 {have}");
            }
        }
        _out.WriteLine($"{rows.Count} 行、{cells} 格，对不上 {bad} 格；未知列 {stats.UnknownColumns.Count}、类型不符 {stats.TypeMismatches.Count}");
        Assert.Equal(0, bad);
        Assert.Empty(stats.UnknownColumns);
        Assert.Empty(stats.TypeMismatches);
    }

    /// <summary>
    /// 产品代码真联网跑一次日常增量（申购日回看一年，一两个请求），设 IPO_SMOKE=1 才跑。
    /// 盯的是离线核对盯不到的那一半：客户端 URL、filter 拼法、翻页、对账。本机抓数据时别跑。
    /// </summary>
    [Fact]
    public async Task 联网冒烟_日常增量行数对得上()
    {
        if (Environment.GetEnvironmentVariable("IPO_SMOKE") != "1") { _out.WriteLine("跳过（设 IPO_SMOKE=1 才联网跑）"); return; }
        var dc = new EastMoneyDataCenterClient(new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var task = new IpoIssueTask(new EastMoneyIpoIssueProvider(dc), _repo);
        task.OnProgress += p => _out.WriteLine(p.Text);
        var r = await task.RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Equal(55.28, Convert.ToDouble(_repo.Get("301686")!["ISSUE_PRICE"]));   // 中塑股份
        Assert.Equal("2026-09-22", _repo.Get("301686")!["LISTING_DATE"]);
    }
}
