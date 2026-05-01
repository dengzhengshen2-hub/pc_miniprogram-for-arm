const { callIotBridge } = require("../../utils/cloud");
const {
  formatDateTime,
  formatRelativeUpdate,
  normalizeOnlineStatus,
  safeJSONStringify,
} = require("../../utils/format");

function buildRows(rawData, app) {
  const debug = rawData.debug || {};

  return {
    summary: {
      displayName: rawData.displayName || rawData.deviceName || "未命名设备",
      online: normalizeOnlineStatus(rawData.onlineStatus),
      latestUpdateText: formatDateTime(rawData.latestPropertyTimeMs),
      latestUpdateRelativeText: formatRelativeUpdate(rawData.latestPropertyTimeMs),
      fetchedAtText: formatDateTime(rawData.fetchedAtMs),
    },
    basicRows: [
      { key: "ProductKey", value: rawData.productKey || "--" },
      { key: "DeviceName", value: rawData.deviceName || "--" },
      { key: "DisplayName", value: rawData.displayName || "--" },
      { key: "Endpoint", value: rawData.endpoint || "--" },
      { key: "Property Topic", value: rawData.propertyPostTopic || "--" },
      { key: "Cloud Env", value: app.globalData.envId || "--" },
      { key: "Cloud Function", value: app.globalData.cloudFunctionName || "--" },
    ],
    requestRows: [
      {
        key: "PropertyStatus RequestId",
        value: (debug.requestIds && debug.requestIds.propertyStatus) || "--",
      },
      {
        key: "DeviceInfo RequestId",
        value: (debug.requestIds && debug.requestIds.deviceInfo) || "--",
      },
      {
        key: "DeviceStatus RequestId",
        value: (debug.requestIds && debug.requestIds.deviceStatus) || "--",
      },
    ],
    durationRows: [
      {
        key: "PropertyStatus",
        value: `${(debug.apiDurationsMs && debug.apiDurationsMs.propertyStatus) || 0} ms`,
      },
      {
        key: "DeviceInfo",
        value: `${(debug.apiDurationsMs && debug.apiDurationsMs.deviceInfo) || 0} ms`,
      },
      {
        key: "DeviceStatus",
        value: `${(debug.apiDurationsMs && debug.apiDurationsMs.deviceStatus) || 0} ms`,
      },
      {
        key: "Total",
        value: `${(debug.apiDurationsMs && debug.apiDurationsMs.total) || 0} ms`,
      },
    ],
    identifierRows: [
      {
        key: "MinTemp",
        value: (debug.propertyIdentifiers && debug.propertyIdentifiers.minTemp) || "--",
      },
      {
        key: "MaxTemp",
        value: (debug.propertyIdentifiers && debug.propertyIdentifiers.maxTemp) || "--",
      },
      {
        key: "CenterTemp",
        value: (debug.propertyIdentifiers && debug.propertyIdentifiers.centerTemp) || "--",
      },
    ],
    lastBridgeError: app.globalData.lastBridgeError || "暂无最近错误",
    propertyListText: safeJSONStringify(debug.propertyList || []),
    rawResponsesText: safeJSONStringify(debug.rawResponses || {}),
  };
}

Page({
  data: {
    loading: false,
    errorMessage: "",
    summary: {
      displayName: "未命名设备",
      online: {
        text: "状态未知",
        tone: "pending",
      },
      latestUpdateText: "--",
      latestUpdateRelativeText: "暂无上报",
      fetchedAtText: "--",
    },
    basicRows: [],
    requestRows: [],
    durationRows: [],
    identifierRows: [],
    lastBridgeError: "暂无最近错误",
    propertyListText: "",
    rawResponsesText: "",
  },

  onLoad() {
    if (!this.ensureAccess()) {
      return;
    }

    this.loadDebugData();
  },

  onShow() {
    if (!this.ensureAccess()) {
      return;
    }

    if (this.rawDebugData) {
      this.applyDebugData(this.rawDebugData);
    }
  },

  onPullDownRefresh() {
    if (!this.ensureAccess()) {
      wx.stopPullDownRefresh();
      return;
    }

    this.loadDebugData({
      fromPullDown: true,
    });
  },

  ensureAccess() {
    const app = getApp();

    if (app.canAccessDebugPage && app.canAccessDebugPage()) {
      return true;
    }

    this.setData({
      errorMessage: "请先在设置页连续点击版本区域 5 次后，再进入调试页。",
    });
    wx.showToast({
      title: "请先在设置页解锁调试页",
      icon: "none",
    });
    wx.switchTab({
      url: "/pages/settings/index",
    });
    return false;
  },

  loadDebugData(options) {
    if (this.data.loading) {
      if (options && options.fromPullDown) {
        wx.stopPullDownRefresh();
      }
      return;
    }

    this.setData({
      loading: true,
      errorMessage: "",
    });

    callIotBridge("getDebugData")
      .then((data) => {
        this.rawDebugData = data;
        this.applyDebugData(data);
      })
      .catch((error) => {
        this.setData({
          errorMessage: error.message || "调试信息加载失败，请稍后重试。",
        });
      })
      .finally(() => {
        this.setData({
          loading: false,
        });

        if (options && options.fromPullDown) {
          wx.stopPullDownRefresh();
        }
      });
  },

  applyDebugData(rawData) {
    const rows = buildRows(rawData, getApp());

    this.setData({
      summary: rows.summary,
      basicRows: rows.basicRows,
      requestRows: rows.requestRows,
      durationRows: rows.durationRows,
      identifierRows: rows.identifierRows,
      lastBridgeError: rows.lastBridgeError,
      propertyListText: rows.propertyListText,
      rawResponsesText: rows.rawResponsesText,
    });
  },
});
