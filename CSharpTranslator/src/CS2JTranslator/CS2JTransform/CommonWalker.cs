﻿/*
   Copyright 2010-2013 Kevin Glynn (kevin.glynn@twigletsoftware.com)
   Copyright 2007-2013 Rustici Software, LLC

This program is free software: you can redistribute it and/or modify
it under the terms of the MIT/X Window System License

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.

You should have received a copy of the MIT/X Window System License
along with this program.  If not, see 

   <http://www.opensource.org/licenses/mit-license>
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Xml;
using System.Xml.Xsl;
using System.IO;

using Antlr.Runtime.Tree;
using Antlr.Runtime;

using AntlrCSharp;

using Twiglet.CS2J.Translator.Utils;
using Twiglet.CS2J.Translator.TypeRep;
using Twiglet.CS2J.Translator;

namespace Twiglet.CS2J.Translator.Transform
{
    public abstract class CommonWalker : TreeParser
    {
       // CONSTANTS

       // Max size of enum structure we will generate to match enums to arbitrary integer values
       public const int MAX_DUMMY_ENUMS = 500;


        public CS2JSettings Cfg { get; set; }
        public string Filename { get; set; }

        // AppEnv contains a summary of the environment within which we are translating, it maps fully qualified type names to their
        // translation templates (i.e. a description of their public features)
        public DirectoryHT<TypeRepTemplate> AppEnv {get; set;}


        protected CommonWalker(ITreeNodeStream input, RecognizerSharedState state)
            : base(input, state)
        { }

        protected void Error(int line, String s)
        {
            throw new Exception(String.Format("{0}({1}) error: {2}", Filename, line, s));
        }

        protected void Error(String s)
        {
           throw new Exception(String.Format("{0} error: {1}", Filename, s));
        }

        protected void Warning(int line, String s)
        {
            if (Cfg.Warnings)
                Console.Out.WriteLine("{0}({1}) warning: {2}", Filename, line, s);
        }

        protected void Warning(String s)
        {
            if (Cfg.Warnings)
                Console.Out.WriteLine("{0} warning: {1}", Filename, s);
        }

        protected void WarningAssert(bool assertion, int line, String s)
        {
            if (Cfg.Warnings && !assertion)
                Console.Out.WriteLine("{0}({1}) failed assertion: {2}", Filename, line, s);
        }

        protected void WarningAssert(bool assertion, String s)
        {
            if (Cfg.Warnings && !assertion)
                Console.Out.WriteLine("{0} failed assertion: {1}", Filename, s);
        }

        protected void WarningFailedResolve(int line, String s)
        {
            if (Cfg.WarningsFailedResolves)
               Console.Out.WriteLine("{0}({1}) warning: {2}", Filename, line, s);
        }

        protected void Debug(String s)
        {
            Debug(1, s);
        }

        protected void DebugDetail(string s)
        {
            Debug(5, s);
        }

        protected void Debug(int level, String s)
        {
            if (level <= Cfg.DebugLevel)
            {
                Console.Out.WriteLine(s);
            }
        }

        // distinguish classes with same name, but differing numbers of type arguments
        // Dictionary<K,V> -> Dictionary'2
        protected string mkGenericTypeAlias (string name, List<String> tyargs) {
            return mkGenericTypeAlias(name, tyargs == null ? 0 : tyargs.Count);
        }
        
        protected string mkGenericTypeAlias (string name, int tyargCount) {
            return name + (tyargCount > 0 ? "'" + tyargCount.ToString() : "");
        }
        
        protected string formatTyargs(List<string> tyargs) {
               
            if (tyargs.Count == 0) {
                return "";
            }
            StringBuilder buf = new StringBuilder();
            buf.Append("<");
            foreach (string t in tyargs) {
                buf.Append(t + ",");
            }
            buf.Remove(buf.Length-1,1);
            buf.Append(">");
            return buf.ToString();
        }

        //  Unless empty return current namespace suffixed with "."
        protected string NSPrefix(string ns) {
            return (String.IsNullOrEmpty(ns) ? "" : ns + ".");
        }

        // Used from parseString() to set up dynamic scopes
        public virtual void InitParser()
        {
        }


        // Routines to substitute in code fragments 
        private class MethodMapper
        {
      
          CommonWalker walker = null;

          public MethodMapper(CommonWalker inWalker) {
            walker = inWalker;
          }       

          public string RewriteMethod(Match m)
          {
            if (walker.Cfg.TranslatorMakeJavaNamingConventions)
            {
              return walker.toJavaConvention(CSharpEntity.METHOD, m.Groups[1].Value);
            }
            return m.Groups[1].Value;
          }
        }


       // Takes a list of type variables to substitute into fragments and returns a string containing the 
       // required methods
       // TypeVars in fragments have names T, T1, T2, T3, etc.
       public string rewriteCodeFragment(string code, IList<string> tyArgs)
       {
          string ret = code;
          MethodMapper mapper = new MethodMapper(this);

          if (tyArgs != null)
          {
             int idx = 0;
             foreach (string t in tyArgs)
             {
                string toReplace = "${T" + (idx == 0 ? "" : Convert.ToString(idx)) + "}";
                ret = ret.Replace(toReplace,tyArgs[idx]);
                idx++;
             }
          }
          // replace @m{<method name>} with (possibly transformed) <method name>
          ret = Regex.Replace(ret, "(?:@m\\{(\\w+)\\})", new MatchEvaluator(mapper.RewriteMethod));

          return ret;
       }



        // Routines to parse strings to ANTLR Trees on the fly, used to generate fragments needed by the transformation
       public CommonTree parseString(string startRule, string inStr)
       {
           return parseString(startRule, inStr, true);
       }

       public CommonTree parseString(string startRule, string inStr, bool includeNet)
       {
            
          if (Cfg.Verbosity > 5) Console.WriteLine("Parsing fragment ");
            
          ICharStream input = new ANTLRStringStream(inStr);

          PreProcessor lex = new PreProcessor();
          lex.AddDefine(Cfg.MacroDefines);
          lex.CharStream = input;
          lex.TraceDestination = Console.Error;

          CommonTokenStream tokens = new CommonTokenStream(lex);

          csParser p = new csParser(tokens);
          p.TraceDestination = Console.Error;
          p.IsJavaish = true;
			
          // Try and call a rule like CSParser.namespace_body() 
          // Use reflection to find the rule to use.
          MethodInfo mi = p.GetType().GetMethod(startRule);

          if (mi == null)
          {
             throw new Exception("Could not find start rule " + startRule + " in csParser");
          }

          ParserRuleReturnScope csRet = (ParserRuleReturnScope) mi.Invoke(p, new object[0]);

          CommonTreeNodeStream csTreeStream = new CommonTreeNodeStream(csRet.Tree);
          csTreeStream.TokenStream = tokens;

          JavaMaker javaMaker = new JavaMaker(csTreeStream);
          javaMaker.TraceDestination = Console.Error;
          javaMaker.Cfg = Cfg;
          javaMaker.IsJavaish = true;
          javaMaker.InitParser();

          // Try and call a rule like CSParser.namespace_body() 
          // Use reflection to find the rule to use.
          mi = javaMaker.GetType().GetMethod(startRule);

          if (mi == null)
          {
             throw new Exception("Could not find start rule " + startRule + " in javaMaker");
          }

          TreeRuleReturnScope javaSyntaxRet = (TreeRuleReturnScope) mi.Invoke(javaMaker, new object[0]);

          CommonTree retAST = (CommonTree)javaSyntaxRet.Tree;

          if (includeNet) {
             CommonTreeNodeStream javaSyntaxNodes = new CommonTreeNodeStream(retAST);
 
             javaSyntaxNodes.TokenStream = csTreeStream.TokenStream;
                     
             NetMaker netMaker = new NetMaker(javaSyntaxNodes);
             netMaker.TraceDestination = Console.Error;
 
             netMaker.Cfg = Cfg;
             netMaker.AppEnv = AppEnv;
             netMaker.IsJavaish = true;
             netMaker.InitParser();
           
             retAST = (CommonTree)netMaker.class_member_declarations().Tree;

             // snaffle additional imports
             this.AddToImports(netMaker.Imports);
          }

          return retAST;
       }

        // If true, then we are parsing some JavaIsh fragment
        private bool isJavaish = false;
	public bool IsJavaish 
	{
		get {
           return isJavaish;
        } 
        set {
           isJavaish = value;
        }
	}

       private Set<string> imports = new Set<string>();
       public Set<string> Imports 
       { 
          get
          {
             return imports;
          }
          set
          {
             imports = value;
          }
       }

       public virtual void AddToImports(string imp) {
          if (!String.IsNullOrEmpty(imp))
          {
             Imports.Add(imp);     
          }
       }

       public void AddToImports(IEnumerable<string> imps) {
          if (imps != null) {
             foreach (string imp in imps) {
                AddToImports(imp);
             }
          }
       }

       protected const string javaDocXslt = @"<?xml version=""1.0""?>

<xsl:stylesheet version=""1.0""
xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"">

<xsl:output method=""text""/>

<xsl:template match=""/"">
 <xsl:apply-templates />
</xsl:template>

<xsl:template match=""c"">
 {@code <xsl:value-of select="".""/>}
</xsl:template>

<xsl:template match=""code"">
 {@code <xsl:value-of select="".""/>}
</xsl:template>

<xsl:template match=""b"">
 <xsl:text>&lt;b></xsl:text><xsl:value-of select="".""/><xsl:text>&lt;/b></xsl:text>
</xsl:template>

<xsl:template match=""see"">
 {@link #<xsl:value-of select=""@cref""/>}
</xsl:template>

<xsl:template match=""paramref"">
 {@code <xsl:value-of select=""@name""/>}
</xsl:template>

<xsl:template match=""summary"">
 <xsl:apply-templates />
</xsl:template>

<xsl:template match=""remarks"">
 <xsl:apply-templates />
</xsl:template>

<xsl:template match=""example"">
 <xsl:apply-templates />
</xsl:template>

<xsl:template match=""value"">
 <xsl:apply-templates />
</xsl:template>

<xsl:template match=""param"">
 @param <xsl:value-of select=""@name""/><xsl:text> </xsl:text> <xsl:apply-templates />
</xsl:template>

<xsl:template match=""exception"">
 @throws <xsl:value-of select=""@cref""/><xsl:text> </xsl:text> <xsl:apply-templates />
</xsl:template>

<xsl:template match=""returns"">
 @return <xsl:apply-templates />
</xsl:template>

<xsl:template match=""seealso"">
 @see <xsl:value-of select=""@cref""/>
</xsl:template>


</xsl:stylesheet>";

       private XslCompiledTransform _jdXslTrans = null;
       protected XslCompiledTransform JdXslTrans
       {
          get
          {
             if (_jdXslTrans == null)
             {
                _jdXslTrans = new XslCompiledTransform();

                // Encode the XML string in a UTF-8 byte array
                byte[] encodedXsltString = Encoding.UTF8.GetBytes(javaDocXslt);

                // Put the byte array into a stream and rewind it to the beginning
                MemoryStream msXslt = new MemoryStream(encodedXsltString);
                msXslt.Flush();
                msXslt.Position = 0;
                _jdXslTrans.Load(XmlReader.Create(msXslt));
             }
             return _jdXslTrans;
          }
       }

       public string toJavaConvention(CSharpEntity type, string str)
       {
          string ret = String.Empty;

          if (str == null || str.Length == 0)
          {
             return ret;
          }

          switch(type)
          {
             case CSharpEntity.METHOD:
                ret = new StringBuilder(str.Length).Append(Char.ToLower(str[0]))
                   .Append(str.Substring(1))
                   .ToString();
                break;
          }
          return ret;
       }

       // Rewrite method names and import locations for LCC variant
       protected string rewriteMethodName(string orig) {
         if (String.IsNullOrEmpty(orig)) 
           return orig;
         if (Cfg.TranslatorMakeJavaNamingConventions) {
             string replacement = toJavaConvention(CSharpEntity.METHOD, orig);
             if (replacement == "main" || replacement == "notify" || replacement == "notifyAll" || replacement == "wait") {
               return orig;
             }
             return replacement; 
           }
         return orig;
       }

       protected string rewriteImportLocation(string orig) {
         if (String.IsNullOrEmpty(orig)) 
           return orig;
         if (Cfg.TranslatorMakeJavaNamingConventions) {
             int dotLoc = orig.LastIndexOf('.');
             if (dotLoc >= 0) {
               return orig.Substring(0,dotLoc) + ".LCC." + orig.Substring(dotLoc+1);
             }
         }
         return orig;
       }

    }
    
   public enum CSharpEntity
   {
      METHOD
   }
       
    // Wraps a compilation unit with its imports search path
    public class CUnit {

        public CUnit(CommonTree inTree, List<string> inSearchPath, List<string> inAliasKeys, List<string> inAliasValues) 
            : this(inTree, inSearchPath, inAliasKeys, inAliasValues, false)
        {
        }

       public CUnit(CommonTree inTree, List<string> inSearchPath, List<string> inAliasKeys, List<string> inAliasValues, bool inIsPartial) {
            Tree = inTree;
            SearchPath = inSearchPath;
            NameSpaceAliasKeys = inAliasKeys;
            NameSpaceAliasValues = inAliasValues;
            IsPartial = inIsPartial;
        }

        public CommonTree Tree {get; set;}

        // namespaces in scope
        public List<string> SearchPath {get; set;}

        //  aliases for namespaces
        public List<string> NameSpaceAliasKeys {get; set;}
        public List<string> NameSpaceAliasValues {get; set;}
        public bool IsPartial {get; set;}
    }

   // This structure holds (serialized) consolidated strings describing
   // partial classes and interfaces 
    public class ClassDescriptorSerialized {
       public String FileName { get;set; }
       public List<String> Imports { get;set; }
       public String Package { get;set; }
       public String Type { get;set; }
       public String Comments { get;set; }
       public String Atts { get;set; }
       public List<String> Mods { get;set; }
       public String Identifier { get;set; }
       public String TypeParameterList { get;set; }
       public List<String> ClassExtends { get;set; }  // Can have multiple extends in an interface
       public List<String> ClassImplements { get;set; }
       public String ClassBody { get;set; }
       public String EndComments { get;set; }

       public Dictionary<String,ClassDescriptorSerialized> PartialTypes { get;set; }
       
       public ClassDescriptorSerialized(string name)
       {
          FileName = "";
          Comments = "";
          Imports = new List<String>();
          Package = "";
          Type = "class";
          Atts = "";
          Mods = new List<String>();
          Identifier = name;
          TypeParameterList = "";
          ClassExtends = new List<String>();
          ClassImplements = new List<String>();
          ClassBody = "";
          EndComments = "";
          PartialTypes = new Dictionary<String,ClassDescriptorSerialized>();
       }
    }

}
