from .logger import MyLogger
from .startup import StartUp
from ..handler.DefaultCommand import DefaultCommand
from ..handler.ChatBox import ChatboxHandler
from ..handler.Avatar import AvatarHandler
import speech_recognition as sr
import requests
from multiprocessing import Process
import winsound

def once(audio:sr.AudioData,baseurl,sendClient,config,headers,params,logger):
    tragetTranslateLanguage=params["tragetTranslateLanguage"]
    sourceLanguage=params["sourceLanguage"]
    avatar=AvatarHandler(logger=logger,osc_client=sendClient,config=config)
    defaultCommand=DefaultCommand(logger=logger,osc_client=sendClient,config=config)
    chatbox=ChatboxHandler(logger=logger,osc_client=sendClient,config=config)
    defaultCommand=DefaultCommand(logger=logger,osc_client=sendClient,config=config)
    try:

        logger.put({"text":"音频输出完毕","level":"info"})
        if params["runmode"] == "control" or params["runmode"] == "text":
            url=baseurl+"/whisper/multitranscription"
        elif params["runmode"] == "translation":
            if config["translationServer"] == "libre":
                if tragetTranslateLanguage=="en" and sourceLanguage== "zh" :url=baseurl+"/func/translateToEnglish"
                elif sourceLanguage != "zh" :url=baseurl+"/func/multitranslateToOtherLanguage"
                else:url=baseurl+"/func/translateToOtherLanguage"
            else:
                url=baseurl+"/func/doubleTransciption"
        else: 
            logger.put({"text":"运行模式异常,运行默认控制模式","level":"debug"})
            params["runmode"] = "control"
            url = baseurl+"/whisper/multitranscription"
        logger.put({"text":f"url:{url},tragetTranslateLanguage:{tragetTranslateLanguage}","level":"debug"})
        files = {'file': ('filename', audio.get_wav_data(), 'audio/wav')}
        data = {'targetLanguage': tragetTranslateLanguage, 'sourceLanguage': sourceLanguage}
        response = requests.post(url, files=files, data=data, headers=headers)
        # 检查响应状态码
        if response.status_code != 200:
            logger.put({"text":f"数据接收异常:{response.text}","level":"warning"})
            return
        # 解析JSON响应
        res = response.json()
        if res["text"] =="":
            logger.put({"text":"返回值过滤","level":"debug"})
            return
        logger.put({"text":"你说的是: " + res["text"],"level":"info"})
        if defaultCommand.handle(res["text"],params=params):return
        if params["runmode"] == "text" or params["runmode"] == "translation": chatbox.handle(res,runMode=params["runmode"])
        if params["runmode"] == "control":avatar.handle(res)
    except requests.JSONDecodeError:
        logger.put({"text":"json解析异常,code:"+str(response.status_code)+" info:"+response.text,"level":"warning"})
        return
    except Exception as e:
        logger.put({"text":e,"level":"warning"})
        return
def threaded_listen(baseurl,sendClient,config,headers,params,logger):
    # logger=MyLogger().logger
    r = sr.Recognizer()
    m = sr.Microphone()
    logger.put({"text":"开始音量测试","level":"info"})
    with m as source:
        r.adjust_for_ambient_noise(source)  # we only need to calibrate once, before we start listening
    logger.put({"text":"结束音量测试","level":"info"})
    logger.put({"text":"sound process started complete||音频进程启动完毕","level":"info"})
    winsound.PlaySound('SystemAsterisk', winsound.SND_ALIAS)
    with m as s:
        while params["running"]:
            try:  # listen for 1 second, then check again if the stop function has been called
                audio = r.listen(s, 10)
            except sr.WaitTimeoutError:  # listening timed out, just try again
                pass
            else:
                if params["running"]:
                    p = Process(target=once,daemon=True, args=(audio,baseurl,sendClient,config,headers,params,logger))
                    p.start()

    logger.put({"text":"sound process exited complete||音频进程退出完毕","level":"info"})
def logger_process(queue):
    logger=MyLogger().logger
    while True:
        text=queue.get()
        if text['level']=="debug":logger.debug(text['text'])
        elif text['level']=="info":logger.info(text['text'])
        elif text['level']=="warning":logger.warning(text['text'])
        elif text['level']=="error":logger.error(text['text'])
        else :logger.warning(text)