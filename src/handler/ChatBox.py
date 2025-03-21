from typing import Optional
from .base_handler import BaseHandler
from .Color import Colors
class ChatboxHandler(BaseHandler):
    def __init__(self,logger, osc_client,config):
        super().__init__(osc_client)
        self.defaultScripts=config["defaultScripts"]
        self.logger=logger
    """聊天框处理器"""
        
    def handle(self, message: str,runMode:str):
        if runMode == "text": self.sendTextFunction(message)
        if runMode == "translation":self.translateFunction(message)
    def translateFunction(self,res:str):
        text=res['text']
        transtext=res.get('translatedText')
        self.logger.put({"text":f"{Colors.CYAN}输出文字: {transtext}({text}){Colors.END}","level":"info"})
        self.osc_client.send_message("/chatbox/input",[ f'{transtext}({text})', True, False])

    def sendTextFunction(self,res:str):
        text=res['text']
        self.logger.put({"text":f"{Colors.CYAN}输出文字: {text}{Colors.END}","level":"info"})
        self.osc_client.send_message("/chatbox/input",[ f'{text}', True, False])   