﻿using System;
using System.IO;
using System.Collections;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using DotNetNuke.Entities.Urls;
using DNN.Modules.NewsArticles.ModuleFriendlyUrlProvider.Entities;
using DotNetNuke.Entities.Modules;

namespace DNN.Modules.NewsArticles.ModuleFriendlyUrlProvider
{
    [Serializable()]
    public class NewsArticlesModuleProvider : ExtensionUrlProvider
    {
        #region private members
        protected bool _initialized = false; //whether portal settings of provider are read in
        protected string _ignoreRedirectRegex;//-a regex pattern to ignore certain Urls that will not be redirected
        FriendlyUrlOptions _options = null; //local copy of the Friendly Url Options, which control things like the inclusion of hyphens instead of spaces, etc.
        //the following set of members are specific to this module, and are used to control how the module provider works.
        //these are read from the web.config file, and appear as attributes in the Module Provider declaration
        protected int _noDnnPagePathTabId = -1; //- used to specify if the  module is on a page where the page path should not be included in the url.
        protected string _urlPath;//- specifies the url path to use for a NewsArticles Url
        protected bool _redirectUrls; //- whether or not to redirect old-style Urls to the new ones
        protected int _startingArticleId; //- article Id to start changes from
        protected string _articleUrlStyle;
        protected string _articleUrlSource;
        protected string _pageUrlStyle;
        protected string _authorUrlStyle;
        protected string _categoryUrlStyle;
        protected bool _isDebug;
        protected bool _isValid;
        private Dictionary<int, TabUrlOptions> _tabUrlOptions;
        #endregion

        #region overridden methods and properties

        private bool IsInitialized()
        {
            //look for an attribute specifying which tab the module
            //will not use a page path for.  There can only be one
            //tab specified per portal (because there is no page path, then it
            //can only be a single page, as the way of determining one dnn
            //page from another isn't in the reuqested Url)
            if (_initialized == false)
            {
                var attributes = this.GetProviderPortalSettings();
                _ignoreRedirectRegex = attributes["ignoreRedirectRegex"];
                //look for an attribute specifying which tab the module
                //will not use a page path for.  There can only be one
                //tab specified per portal (because there is no page path, then it
                //can only be a single page, as the way of determining one dnn
                //page from another isn't in the reuqested Url)
                string noDnnPagePathTabRaw = attributes["noDnnPagePathTabId"];
                int.TryParse(noDnnPagePathTabRaw, out _noDnnPagePathTabId);

                _urlPath = attributes["urlPath"];
                bool.TryParse(attributes["redirectUrls"], out _redirectUrls);
                string startingArticleIdRaw = attributes["startingArticleId"];
                if (!int.TryParse(startingArticleIdRaw, out _startingArticleId))
                    _startingArticleId = 0;

                //read in the different url styles for this instance           
                _tabUrlOptions = new Dictionary<int, TabUrlOptions>();
                Hashtable articleUrlStyles = TabUrlOptions.GetHashTableFromSetting(attributes["articleUrlStyle"], out _articleUrlStyle);
                Hashtable articleUrlSource = TabUrlOptions.GetHashTableFromSetting(attributes["articleUrlSource"], out _articleUrlSource);
                Hashtable pageUrlStyles = TabUrlOptions.GetHashTableFromSetting(attributes["pageUrlStyle"], out _pageUrlStyle);
                Hashtable authorUrlStyles = TabUrlOptions.GetHashTableFromSetting(attributes["authorUrlStyle"], out _authorUrlStyle);
                Hashtable categoryUrlStyles = TabUrlOptions.GetHashTableFromSetting(attributes["categoryUrlStyle"], out _categoryUrlStyle);

                //if (this.AllTabs)
                _tabUrlOptions.Add(-1, new TabUrlOptions(-1, _startingArticleId, articleUrlStyles, articleUrlSource, pageUrlStyles, authorUrlStyles, categoryUrlStyles));
                //foreach (int tabId in this.TabIds)
                //{
                //    //create a tab option for each set tab 
                //    TabUrlOptions urlOptions = new TabUrlOptions(tabId, _startingArticleId, articleUrlStyles, articleUrlSource, pageUrlStyles, authorUrlStyles, categoryUrlStyles);
                //    _tabUrlOptions.Add(tabId, urlOptions);
                //}
                //_initialized = true;
            }
            return _initialized;

        }

        /// <summary>
        /// Constructor for the NewsArticles Url Provider.  This is called by the Url Master module when loading the Provider
        /// </summary>
        /// <param name="name">Name is supplied from the web.config file, and specifies the unique name of the provider</param>
        /// <param name="attributes">Attributes are the xml attributes from the file</param>
        /// <param name="portalId">The portalId is supplied for the calling portal.  Each instance of the provider is portal specific.</param>
        public NewsArticlesModuleProvider()
            : base()
        {
            
        }
        /// <summary>
        /// The Change Friendly Url method is called for every Url generated when a page is generated by DotNetNuke.  This call sits 'underneath' the 'NavigateUrl' call in DotNetNuke.
        /// Whenever your module calls NavigateUrl, this method will be also called.  In here, the module developer should modify the friendlyUrlPath to the final state required.
        /// However, because this call is used for all Urls on the page, not just those generated by the target module, some type of high-level filter should be used to make sure only
        /// the module Urls are modified.
        /// 
        /// </summary>
        /// <param name="tab">Current Tab</param>
        /// <param name="friendlyUrlPath">Current Friendly Url Path after going through the Friendly Url Generation process of the Url Master module.</param>
        /// <param name="options">The options currently applying to Urls in this portal (space replacement, max length, etc)</param>
        /// <param name="cultureCode">The culture code being used for this Url (if supplied, may be empty)</param>
        /// <param name="endingPageName">The page name the Url has been called with. Normally default.aspx, but may be different for some modules.</param>
        /// <param name="useDnnPagePath">Out parameter to be set by the module.  If true, the path of the DNN page will be in the Url (ie /pagename).  If false, this part of the Url will be removed. </param>
        /// <param name="messages">List of debug messages.  Add any debug information to this collection to help debug your provider.  This can be seen in the repsonse headers, and also in the 'test Url Rewrite' page in the Url Master module.</param>
        /// <returns></returns>
        public override string ChangeFriendlyUrl(DotNetNuke.Entities.Tabs.TabInfo tab, string friendlyUrlPath, FriendlyUrlOptions options, string cultureCode, ref string endingPageName, out bool useDnnPagePath, ref List<string> messages)
        {
            _options = options;//keep local copy of options
            TabUrlOptions urlOptions = GetTabUrlOptions(tab.TabID);
            //set default values for out parameters
            useDnnPagePath = true;
            if (messages == null) messages = new List<string>();
            //check if we want to try and modify this Url
            //first check to see if this Url is an 'edit' Url - something that loads a module-specific page.
            //we don't want to mess with these, because they're always permissions based Urls and thus
            //no need to be friendly
            if (Regex.IsMatch(friendlyUrlPath, @"(^|/)(mid|moduleId)/\d+/?", RegexOptions.IgnoreCase) == false)
            {
                //try and match incoming friendly url path to what we would expect from the module
                //NOTE: regex used here but can be any type of logic to determine if this is a friendly url that applies to the module
                //There will be many different urls created for this tab, so we only want to change the ones we know apply to this module
                //by way of looking for a certain pattern or other unique marker in the Url.
                //For this example, match by looking for 'articleId' - normally the Url would be /pagename/tabid/xx/articleId/yy/default.aspx
                bool replacementFound = false;
                if (!replacementFound)
                {
                    //redundant if, but blocks together statements for consistency and localises variables
                    //matches category and page optionally, because these are sometime-addendums to the base article ID
                    Regex articleUrlRegex = new Regex(@"((?<l>/)?articleType/ArticleView/articleId/|/id/)(?<artid>\d+)(/PageId/(?<pageid>\d+))?(/categoryId/(?<catid>\d+))?", RegexOptions.IgnoreCase);
                    Match articleUrlMatch = articleUrlRegex.Match(friendlyUrlPath);

                    if (articleUrlMatch.Success)
                    {
                        string articleUrl = "";
                        replacementFound = UrlController.MakeArticleUrl(this, articleUrlMatch, articleUrlRegex, friendlyUrlPath, tab, options, urlOptions, cultureCode, ref endingPageName, ref useDnnPagePath, ref messages, out articleUrl);
                        if (replacementFound)
                            friendlyUrlPath = articleUrl;
                    }
                }

                if (!replacementFound)
                {
                    //no match on article  - next check is for a category match 
                    Regex authorUrlRegex = new Regex(@"(?<l>/)?articleType/AuthorView/authorId/(?<authid>\d+)", RegexOptions.IgnoreCase);
                    Match authorUrlMatch = authorUrlRegex.Match(friendlyUrlPath);
                    if (authorUrlMatch.Success)
                    {
                        string authorUrl = "";
                        replacementFound = UrlController.MakeAuthorUrl(this, authorUrlMatch, authorUrlRegex, friendlyUrlPath, tab, options, urlOptions, cultureCode, ref endingPageName, ref useDnnPagePath, ref messages, out authorUrl);
                        if (replacementFound)
                            friendlyUrlPath = authorUrl;
                    }
                }
                if (!replacementFound)
                {
                    //no match on article  - next check is for a category match 
                    Regex categoryUrlRegex = new Regex(@"(?<l>/)?articleType/CategoryView/categoryId/(?<catid>\d+)", RegexOptions.IgnoreCase);
                    Match categoryUrlMatch = categoryUrlRegex.Match(friendlyUrlPath);
                    if (categoryUrlMatch.Success)
                    {
                        string categoryUrl = "";
                        replacementFound = UrlController.MakeCategoryUrl(this, categoryUrlMatch, categoryUrlRegex, friendlyUrlPath, tab, options, urlOptions, cultureCode, ref endingPageName, ref useDnnPagePath, ref messages, out categoryUrl);
                        if (replacementFound)
                            friendlyUrlPath = categoryUrl;
                    }

                }
                if (!replacementFound)
                {
                    Regex archiveUrlRegex = new Regex(@"(?<l>/)?articleType/ArchiveView(?<mth>/month/(?<mm>\d+))?(?<yr>/year/(?<yyyy>\d+))?", RegexOptions.IgnoreCase);
                    Match archiveUrlMatch = archiveUrlRegex.Match(friendlyUrlPath);
                    if (archiveUrlMatch.Success)
                    {
                        string archiveUrl = "";
                        replacementFound = UrlController.MakeArchiveUrl(this, archiveUrlMatch, archiveUrlRegex, friendlyUrlPath, tab, options, urlOptions, cultureCode, ref endingPageName, ref useDnnPagePath, ref messages, out archiveUrl);
                        if (replacementFound)
                            friendlyUrlPath = archiveUrl;
                    }
                }
                if (replacementFound)
                    friendlyUrlPath = base.EnsureLeadingChar("/", friendlyUrlPath);
            }
            return friendlyUrlPath;
        }
        

        /// <summary>
        /// This method is used by the Url Master Url Rewriting process.  The purpose of this method is to take the supplied array of Url parameters, and transform them into a module-specific querystring for the underlying re-written Url.
        /// </summary>
        /// <param name="urlParms">The array of parameters found after the DNN page path has been identified.  No key/valeu pairs are identified, the parameters are converted from the /key/value/key2/value2 format into [key,value,key2,value2] format.</param>
        /// <param name="tabId">TabId of identified DNN page. </param>
        /// <param name="portalId">PortalId of identified DNN portal.</param>
        /// <param name="options">The current Friendly Url options being used by the module.</param>
        /// <param name="cultureCode">Identified language/culture code, if supplied.</param>
        /// <param name="portalAlias">Identified portalAlias object for the request.</param>
        /// <param name="messages">List of debug messages.  Add to this list to help debug your module.  Can be viewed in the reponse headers of the request, or in the 'Test Url Rewriting' section of the Url Master module.</param>
        /// <param name="status">Out parameter, returns the Http status of the request.  May be 200,301,302, or 404.  For normal rewriting, return a 200 value.</param>
        /// <param name="location">If a 301 or 302 is returned in the status parameter, then this must contain a valid redirect location.  This should be a fully-qualified Url.</param>
        /// <returns>The querystring to be used for rewriting the Url. NOTE: doesn't need to include the tabid if the tabid parameter is > -1</returns>
        public override string TransformFriendlyUrlToQueryString(string[] urlParms, int tabId, int portalId, FriendlyUrlOptions options, string cultureCode, DotNetNuke.Entities.Portals.PortalAliasInfo portalAlias, ref List<string> messages, out int status, out string location)
        {
            string path = string.Join("/", urlParms);
            //initialise results and output variables
            location = null; //no redirect location
            if (messages == null) messages = new List<string>();
            string result = ""; status = 200; //OK 
            //prevent incorrect matches of Urls
            if (!Regex.IsMatch(path, @"(articleType/(?<type>[^/]+))|(ctl/[^/]+/(mid|moduleid)/\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                //store local options variable
                _options = options;
                //get the tab options
                TabUrlOptions urlOptions = GetTabUrlOptions(tabId);
                Hashtable queryStringIndex = null;
                int skipUpToIndex = -1;
                bool found = false;
                bool siteRootMatch = false;
                bool tabHasNAModule = false;
                //foreach (ModuleInfo mi in ModuleController.Instance.GetTabModules(tabId).Values) {

                //}
                if ((from ModuleInfo mi in ModuleController.Instance.GetTabModules(tabId).Values where mi.DesktopModule.FolderName.ToLower().Contains("dnnforge - newsarticles") select mi.ModuleTitle).Count() != 0) {
                    tabHasNAModule = true;
                }
                
                //look for match on pattern for date and title - the pattern used by this provider
                //string path = string.Join("/", urlParms);
                //messages.Add("Checking for Items in Friendly Url path: " + path);
                if (urlParms.Length > 0)
                {
                    //tabid == -1 when no dnn page path is in the Url.  This means the Url Master module can't determine the DNN page based on the Url.
                    //In this case, it is up to this provider to identify the correct tabId that matches the Url.  Failure to do so will result in the incorrect tab being loaded when the page is rendered. 
                    if (tabId == -1)
                    {
                        siteRootMatch = true;
                        if (_noDnnPagePathTabId > -1)
                            //tabid -1 means a 'site root' match - meaning that the dnn page path wasn't included in the Url
                            tabId = _noDnnPagePathTabId;//if tabid = -1, it means a site root match (no dnn page path) so we substitute in the tabid where this is being used
                    }
                    queryStringIndex = UrlController.GetQueryStringIndex(tabId, portalId, this, options, urlOptions, false);
                    string pathBasedKey = string.Join("/", urlParms).ToLower();
                    string qs = null;
                    if (queryStringIndex.ContainsKey(pathBasedKey))
                    {
                        //that was easy-  direct match
                        qs = (string)queryStringIndex[pathBasedKey];
                        skipUpToIndex = urlParms.GetUpperBound(0);
                    }
                    else
                    {
                        //go through the parameter list backwards until we find a match
                        for (int i = urlParms.GetUpperBound(0); i >= 0; i--)
                        {
                            //copy all the array minus the i index item
                            int tempLength = i + 1;
                            string[] tempParms = new string[tempLength];
                            Array.Copy(urlParms, 0, tempParms, 0, i + 1);
                            //make a new key from the shorter array
                            pathBasedKey = string.Join("/", tempParms).ToLower();
                            //check if that matches
                            if (queryStringIndex.ContainsKey(pathBasedKey))
                            {
                                qs = (string)queryStringIndex[pathBasedKey];
                                if (qs != null)
                                {
                                    //the trimmed pieces need to be included
                                    skipUpToIndex = i;
                                    break;
                                }
                            }
                        }
                    }
                    if (qs != null)
                    {
                        //found a querystring match
                        found = true;
                        messages.Add("Item Matched in Friendly Url Provider.  Url : " + pathBasedKey + " Path : " + path);
                        result += qs;
                    }
                    else
                    {
                        //no match, but look for a date archive pattern
                        //903 : issue with matching other Urls that aren't archive Urls
                        Regex archivePatternRegex = new Regex(@"(?<!year)(?<yr>(^|/)(?<yyyy>[\d]{4}))(?<mth>/(?<mm>[\d]{1,2}))?", RegexOptions.IgnoreCase);
                        Match archivePatternMatch = archivePatternRegex.Match(path);
                        if (archivePatternMatch.Success)
                        {
                            bool month = false, year = false;
                            string mm = null, yyyy = null;
                            //matched on date pattern, extract month/year
                            Group mthGrp = archivePatternMatch.Groups["mth"];
                            if (mthGrp != null && mthGrp.Success)
                            {
                                mm = archivePatternMatch.Groups["mm"].Value;
                                month = true;
                            }
                            Group yrGrp = archivePatternMatch.Groups["yyyy"];
                            if (yrGrp != null && yrGrp.Success)
                            {
                                //902 : don't allow invalid dates to be passed down
                                int yearVal = 0;
                                yyyy = yrGrp.Value;
                                //check that year is a valid int, and that year is later than sql min date time
                                if (int.TryParse(yyyy, out yearVal) && yearVal > 1753 && tabHasNAModule)
                                {
                                    year = true;
                                }
                            }
                            if (year)
                            {
                                qs = "";
                                if (this.NoDnnPagePathTabId == tabId)
                                    qs += "?tabid=" + tabId.ToString();
                                //add on the year
                                qs += "&articleType=ArchiveView&year=" + yyyy;
                                skipUpToIndex = 0;//1st position
                            }
                            if (year && month)
                            {
                                int mmVal = 0;
                                if (int.TryParse(mm, out mmVal) && mmVal > 0 && mmVal < 13)
                                {
                                    qs += "&month=" + mm;
                                    skipUpToIndex = 1;//2nd position 
                                }
                            }
                            if (year || month)
                            {
                                result += qs;
                            }
                        }
                    }

                }

                if (skipUpToIndex >= 0)
                {
                    //put on any remainder of the path that wasn't to do with the friendly Url
                    //but only if there was *something* in the friendly url that we interpreted
                    string remainder = base.CreateQueryStringFromParameters(urlParms, skipUpToIndex);
                    //put it all together for the final rewrite string
                    result += remainder;
                }
            }
            return result;

        }

        /// <summary>
        /// Determines when to do a redirect.  This is separate to the rewriting process.  The module developer can create any type of Url redirect here, because the entire Url of the original request is passed in.
        /// </summary>
        /// <param name="tabId">Identified TabId, if known.  -1 if no valid tabid identified.</param>
        /// <param name="portalid">Identified portalId.</param>
        /// <param name="httpAlias">Identified httpAlias of the request.</param>
        /// <param name="requestUri">The original requested Url</param>
        /// <param name="queryStringCol">The querystring collection of the original request</param>
        /// <param name="options">The friendly url options that currently apply.</param>
        /// <param name="redirectLocation">Out parameter that shows where to redirect to.</param>
        /// <param name="messages">List of messages for debug purposes.  Add to this list to help debug your module.</param>
        /// <returns>true if 301 redirect is required, false if not.  If true, the redirectLocation value must be a valid fully qualified Url.</returns>
        public override bool CheckForRedirect(int tabId, int portalid, string httpAlias, Uri requestUri, System.Collections.Specialized.NameValueCollection queryStringCol, FriendlyUrlOptions options, out string redirectLocation, ref List<string> messages)
        {
            bool doRedirect = false;
            if (messages == null) messages = new List<string>();
            redirectLocation = "";//set blank location
            //compare to known pattern of old Urls
            if (_redirectUrls)
            {
                Regex oldNewsRegex = new Regex(@"(&articleType=(?<type>[^&]+))?((&(?<idname>[a-z]*Id)=(?<id>\d+))|((&month=(?<mm>[\d]{1,2}))?&year=(?<yyyy>[\d]{4})))(&(?<pgname>PageId|CurrentPage)=(?<pg>[\d]+))?", RegexOptions.IgnoreCase);
                Match oldNewsMatch = oldNewsRegex.Match(queryStringCol.ToString());
                if (oldNewsMatch.Success)
                {
                    Group typeGroup = oldNewsMatch.Groups["type"];
                    Group idNameGroup = oldNewsMatch.Groups["idname"];
                    Group idGroup = oldNewsMatch.Groups["id"];
                    Group pageGroup = oldNewsMatch.Groups["pg"];
                    Group pgNameGrp = oldNewsMatch.Groups["pgname"];
                    string msg = "";
                    string id = null;
                    string furlKey = null;
                    string friendlyUrl = null;
                    if (idGroup != null && idGroup.Success)
                        id = idGroup.Value;
                    string idType = null;
                    if (typeGroup != null && typeGroup.Success)
                    {
                        idType = typeGroup.Value.ToLower();
                    }
                    else
                    {
                        if (idNameGroup != null && idNameGroup.Success)
                        {
                            //check if it's the 'ID' value
                            if (idNameGroup.Value.ToLower() == "id")
                                idType = "id";
                        }
                    }
                    //now look at the idType
                    string pagePath = null;
                    if (pgNameGrp != null && pgNameGrp.Success == true && pageGroup != null && pageGroup.Success)
                    {
                        pagePath = pgNameGrp.Value + "/" + pageGroup.Value;
                    }
                    switch (idType)
                    {
                        case "articleview":
                        case "id":
                            msg = "Identified as old-style news article";
                            //article
                            if (pageGroup != null && pageGroup.Success)
                            {
                                furlKey = "p" + pageGroup.Value;
                                pagePath = null; //taking care of page separately
                            }
                            else
                            {
                                int articleId = -1;
                                //only for items that are in the range of allowed article ids
                                if (int.TryParse(id, out articleId))
                                {
                                    if (articleId >= this.StartingArticleId)
                                        furlKey = "a" + id;
                                }
                            }

                            break;
                        case "categoryview":
                            msg = "Identified as old-style news category";
                            furlKey = "c" + id;
                            break;
                        case "archiveview":
                            //get the mm and yyyy
                            msg = "Identified as old-style news archive";
                            Group yyyyGrp = oldNewsMatch.Groups["yyyy"];
                            if (yyyyGrp != null && yyyyGrp.Success)
                            {
                                string yyyy = yyyyGrp.Value;
                                string mm = null;
                                Group mmGrp = oldNewsMatch.Groups["mm"];
                                if (mmGrp != null && mmGrp.Success)
                                {
                                    mm = mmGrp.Value;
                                }
                                friendlyUrl = yyyy;
                                if (mm != null)
                                    friendlyUrl += "/" + mm;
                            }
                            break;
                        case "authorview":
                            msg = "Identified as old-style news author";
                            furlKey = "u" + id;
                            break;
                    }
                    if (furlKey != null)
                    {
                        //now lookup the friendly url index
                        TabUrlOptions urlOptions = GetTabUrlOptions(tabId);
                        Hashtable friendlyUrlIndex = UrlController.GetFriendlyUrlIndex(tabId, portalid, this, options, urlOptions);
                        if (friendlyUrlIndex != null && friendlyUrlIndex.ContainsKey(furlKey))
                        {
                            //look up the index for the item if we don't already have a friendly Url
                            friendlyUrl = (string)friendlyUrlIndex[furlKey];
                        }
                    }
                    if (friendlyUrl != null)
                    {
                        //now merge with the friendly url for the selected page
                        DotNetNuke.Entities.Portals.PortalAliasInfo alias = DotNetNuke.Entities.Portals.PortalAliasController.GetPortalAliasInfo(httpAlias);
                        DotNetNuke.Entities.Portals.PortalSettings ps = new DotNetNuke.Entities.Portals.PortalSettings(tabId, alias);
                        if (pagePath != null)
                            friendlyUrl += this.EnsureLeadingChar("/", pagePath);
                        string baseUrl = "";
                        if (_noDnnPagePathTabId == tabId)
                        {
                            baseUrl = requestUri.Scheme + Uri.SchemeDelimiter + httpAlias + this.EnsureLeadingChar("/", friendlyUrl) + options.PageExtension;//put onto http Alias with no page path
                        }
                        else
                            baseUrl = DotNetNuke.Common.Globals.NavigateURL(tabId, ps, "", friendlyUrl); //add on with page path
                        if (baseUrl != null)
                        {
                            redirectLocation = baseUrl;
                            doRedirect = true;
                            msg += ", found friendly url " + friendlyUrl + ", redirecting";
                            messages.Add(msg);
                        }
                    }

                }

            }
            return doRedirect;
        }

        //public override string FriendlyName
        //{
        //    get { return "iFinity NewsArticles Friendly Url Provider"; }
        //}
        /// <summary>
        /// Returns any custom settings that are specific per-portal for this instance
        /// </summary>
        /// <remarks>
        /// This is used to write the values back out to the web.config from the Url Master UI
        /// </remarks>
        /// <returns>A Dictionary of the key/value pairs of the settings used in this provider.  Return empty dictionary if no portal specific options.</returns>
        public override Dictionary<string, string> GetProviderPortalSettings()
        {
            //returns the settings specific to this portal instance
            Dictionary<string, string> settings = new Dictionary<string, string>();
            if (_urlPath != null && _urlPath != "")
                settings.Add("urlPath", _urlPath);
            else
                settings.Add("urlPath", null);//remove the null values

            if (_noDnnPagePathTabId > 0)
                settings.Add("noDnnPagePathTabId", _noDnnPagePathTabId.ToString());
            else
                settings.Add("noDnnPagePathTabId", null);

            settings.Add("redirectUrls",_redirectUrls.ToString());
            settings.Add("startingArticleId", _startingArticleId.ToString());
            settings.Add("articleUrlStyle",  _articleUrlStyle);
            settings.Add("articleUrlSource", _articleUrlSource);
            settings.Add("pageUrlStyle",  _pageUrlStyle);
            settings.Add("authorUrlStyle", _authorUrlStyle);
            settings.Add("categoryUrlStyle",  _categoryUrlStyle);
            return settings;
        }

        public override bool AlwaysUsesDnnPagePath(int portalId)
        {
            if (_noDnnPagePathTabId > -1)
            {
                return false;//if there is an exception, then return false
            }
            else
                return true;//no specific exceptions, return true every time
        }
        //public override bool IsLicensed(Uri requestUri, out string messageHtml, out string debugMessage)
        //{
        //    //If this module had a licensing requirement, it would be checked here.  If not licensed
        //    //return false, and return some Html to be included on any pages where the provider is used, ie:
        //    //messageHtml = "<div>Hey! this isn't licensed!</div>"; debugMessage = "Fake licence message";
        //    messageHtml = null;
        //    debugMessage = null;
        //    return true;
        //}
        /// <summary>
        /// Returns the Path of the Settings .ascx control (if supplied).  The Settings control is loaded by the Portal Urls page for the provider,
        /// and allows interaction of the settings by the end user, at a per-portal level.
        /// </summary>
        /// <remarks>
        /// Return empty or null string if no control exists.  The path should match the path specified in the DNN install manifest, and 
        /// point to an actual file.
        /// </remarks>
        //public override string SettingsControlSrc
        //{
        //    get { return "DesktopModules/iFinity.NewsArticlesFriendlyUrlProvider/Settings.ascx"; }
        //}
        

#endregion
        #region internal methods and properties
        internal new string CleanNameForUrl(string title, FriendlyUrlOptions options)
        {
            bool replaced = false;
            //the base module Url PRovider contains a Url cleaning routine, which will remove illegal and unwanted characters from a string, using the specific friendly url options
            return base.CleanNameForUrl(title, options, out replaced);
        }
        

        internal int NoDnnPagePathTabId {
            get { return _noDnnPagePathTabId; }
            set { _noDnnPagePathTabId = value; }
        }

        internal string UrlPath {
            get { return _urlPath; }
            set { _urlPath = value; }
        }

        internal int StartingArticleId
        {
            get
            {
                return _startingArticleId;
            }
            set
            {
                _startingArticleId = value;
            }
        }
        internal new string EnsureLeadingChar(string p, string path)
        {
            return base.EnsureLeadingChar(p, path);
        }

        internal TabUrlOptions GetTabUrlOptions(int tabId)
        {
            TabUrlOptions result = null;
            if (_tabUrlOptions != null)
                if (_tabUrlOptions.ContainsKey(tabId))
                    result = _tabUrlOptions[tabId];
                else
                    if (tabId == -1 && _noDnnPagePathTabId > 0)
                    {
                        if (_tabUrlOptions.ContainsKey(_noDnnPagePathTabId))
                            result = _tabUrlOptions[_noDnnPagePathTabId];
                        else
                            if (_tabUrlOptions.ContainsKey(-1))
                                result = _tabUrlOptions[-1];
                    }
                    else
                        //929 : default option if no tab Id specified settings
                        if (_tabUrlOptions.ContainsKey(-1))
                            result = _tabUrlOptions[-1];
            return result;
        }
        internal string ArticleUrlStyle { set { _articleUrlStyle = value;} }
        internal string ArticleUrlSource {  set { _articleUrlSource = value; } }
        internal string PageUrlStyle { set { _pageUrlStyle = value; } }
        internal string AuthorUrlStyle { set { _authorUrlStyle = value; } }
        internal string CategoryUrlStyle { set { _categoryUrlStyle = value; } }
        internal bool RedirectUrls { get { return _redirectUrls;} set {_redirectUrls = value;}}
        

        #endregion
    }
}
