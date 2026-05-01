const ALIYUN_IOT_CONFIG = {
  accessKeyId: "",
  accessKeySecret: "",
  regionId: "cn-shanghai",
  endpoint: "iot.cn-shanghai.aliyuncs.com",
  iotInstanceId: "iot-06z00fpcn2h806i",
  productKey: "",
  deviceName: "",
  apiVersion: "2018-01-20",
  requestTimeoutMs: 8000,
  propertyIdentifiers: {
    minTemp: "MinTemp",
    maxTemp: "MaxTemp",
    centerTemp: "CenterTemp",
  },
};

const WEATHER_CONFIG = {
  provider: "uapis",
  baseUrl: "https://uapis.cn/api/v1/misc/weather",
  apiKey: "",
  defaultLocation: "佛山",
  defaultAdcode: "",
  lang: "zh",
  requestTimeoutMs: 8000,
  enableExtended: true,
  enableForecast: true,
  enableHourly: true,
  enableIndices: true,
};

module.exports = {
  ALIYUN_IOT_CONFIG,
  WEATHER_CONFIG,
};
